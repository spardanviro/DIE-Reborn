using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// Windows 防火墙要放行哪些端口、现在的规则够不够、不够的话跑哪几条 netsh。
    ///
    /// 读规则 (要 COM) 和提权执行在 <c>Firewall</c> 里; 这里只有判断, 有测试。
    ///
    /// ## 除了"有没有放行规则", 还要看"有没有拦截规则"
    ///
    /// 第一次运行 DIE.Server.exe 时 Windows 会弹"是否允许访问网络", 点了取消 (或者弹窗被
    /// 远程桌面挡住没看见), 系统就给这个程序记一条**入站阻止**规则。阻止优先于允许 ——
    /// 端口规则加得再全也进不来。所以发现针对这个程序的阻止规则, 要一起删掉。
    /// </summary>
    public static class FirewallPlan
    {
        /// <summary>和「开放防火墙.cmd」用同样的规则名, 两边谁加的都认。</summary>
        public const string TcpRuleName = "DIE_Reborn TCP";
        public const string UdpRuleName = "DIE_Reborn UDP";

        public const int ProtocolTcp = 6;
        public const int ProtocolUdp = 17;
        public const int ProtocolAny = 256;

        /// <summary>一条防火墙规则里我们关心的那几项 (来自 HNetCfg.FwPolicy2)。</summary>
        public sealed class Rule
        {
            public string Name { get; set; }
            public bool Enabled { get; set; }
            public bool Inbound { get; set; }
            public bool Allow { get; set; }
            public int Protocol { get; set; }
            public string LocalPorts { get; set; }
            public string Application { get; set; }
        }

        /// <summary>要放行的一组端口。</summary>
        public sealed class Need
        {
            public Need(int protocol, int first, int last)
            {
                Protocol = protocol;
                First = first;
                Last = last;
            }

            public int Protocol { get; }
            public int First { get; }
            public int Last { get; }

            public string Label => (Protocol == ProtocolTcp ? "TCP " : "UDP ")
                                   + (First == Last ? First.ToString(CultureInfo.InvariantCulture)
                                       : First + "-" + Last);
        }

        public sealed class Result
        {
            /// <summary>每组端口放没放行。</summary>
            public List<(Need Need, bool Allowed)> Ports { get; } = new List<(Need, bool)>();

            /// <summary>针对服务端程序的入站阻止规则 (名字)。</summary>
            public List<string> BlockingRules { get; } = new List<string>();

            public bool AllGood => Ports.All(p => p.Allowed) && BlockingRules.Count == 0;
        }

        public static List<Need> Needs(int requestPort, int matchPort, int statusPort, int udpFirst, int udpLast)
        {
            List<Need> needs = new List<Need>
            {
                new Need(ProtocolTcp, requestPort, requestPort),
                new Need(ProtocolTcp, matchPort, matchPort),
            };

            if (statusPort > 0)
            {
                needs.Add(new Need(ProtocolTcp, statusPort, statusPort));
            }

            needs.Add(new Need(ProtocolUdp, udpFirst, udpLast));
            return needs;
        }

        /// <param name="firewallOn">当前网络类型下防火墙开没开。关着就什么都不拦。</param>
        /// <param name="serverExe">DIE.Server.exe 的完整路径 —— 只针对别的程序的规则不算数。</param>
        public static Result Evaluate(IEnumerable<Rule> rules, IReadOnlyList<Need> needs, bool firewallOn,
                                      string serverExe)
        {
            Result result = new Result();
            List<Rule> active = rules.Where(r => r.Enabled && r.Inbound).ToList();

            foreach (Rule rule in active.Where(r => !r.Allow && IsProgram(r.Application, serverExe)))
            {
                result.BlockingRules.Add(rule.Name ?? string.Empty);
            }

            List<Rule> allows = active
                .Where(r => r.Allow && (string.IsNullOrEmpty(r.Application) || IsProgram(r.Application, serverExe)))
                .ToList();

            foreach (Need need in needs)
            {
                bool allowed = !firewallOn || Covered(allows, need);
                result.Ports.Add((need, allowed));
            }

            if (!firewallOn)
            {
                result.BlockingRules.Clear();
            }

            return result;
        }

        private static bool Covered(List<Rule> allows, Need need)
        {
            List<(int First, int Last)> ranges = allows
                .Where(r => r.Protocol == need.Protocol || r.Protocol == ProtocolAny)
                .SelectMany(r => ParsePorts(r.LocalPorts))
                .OrderBy(r => r.First)
                .ToList();

            // 合起来能不能盖住 [First, Last]
            int next = need.First;
            foreach ((int first, int last) in ranges)
            {
                if (first > next)
                {
                    break;
                }

                if (last >= next)
                {
                    next = last + 1;
                }

                if (next > need.Last)
                {
                    return true;
                }
            }

            return next > need.Last;
        }

        /// <summary>"1555,2555,50000-50099" / "*" / "Any" -&gt; 区间列表。认不出的项忽略。</summary>
        public static List<(int First, int Last)> ParsePorts(string localPorts)
        {
            List<(int, int)> ranges = new List<(int, int)>();
            string text = (localPorts ?? string.Empty).Trim();

            if (text.Length == 0 || text == "*" || text.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                ranges.Add((0, 65535));
                return ranges;
            }

            foreach (string part in text.Split(','))
            {
                string item = part.Trim();
                int dash = item.IndexOf('-');

                if (dash > 0
                    && int.TryParse(item.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int first)
                    && int.TryParse(item.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int last))
                {
                    ranges.Add((Math.Min(first, last), Math.Max(first, last)));
                }
                else if (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single))
                {
                    ranges.Add((single, single));
                }
            }

            return ranges;
        }

        private static bool IsProgram(string application, string serverExe)
        {
            if (string.IsNullOrEmpty(application) || string.IsNullOrEmpty(serverExe))
            {
                return false;
            }

            try
            {
                return string.Equals(System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(application)),
                    System.IO.Path.GetFullPath(serverExe), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException
                                           || ex is System.IO.PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// 修好它要跑的 netsh 命令 (每项是 netsh 的参数)。先删同名再加, 跑几遍都一样;
        /// 有针对服务端程序的阻止规则就把这个程序的入站规则全删掉 (它不需要程序级规则, 端口规则就够)。
        /// </summary>
        public static List<string> FixCommands(IReadOnlyList<Need> needs, Result current, string serverExe)
        {
            string tcpPorts = string.Join(",", needs.Where(n => n.Protocol == ProtocolTcp).Select(PortText));
            string udpPorts = string.Join(",", needs.Where(n => n.Protocol == ProtocolUdp).Select(PortText));

            List<string> commands = new List<string>();

            if (current != null && current.BlockingRules.Count > 0 && IsSafeForCommand(serverExe))
            {
                commands.Add($"advfirewall firewall delete rule name=all dir=in program=\"{serverExe}\"");
            }

            if (tcpPorts.Length > 0)
            {
                commands.Add($"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
                commands.Add($"advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow "
                             + $"protocol=TCP localport={tcpPorts}");
            }

            if (udpPorts.Length > 0)
            {
                commands.Add($"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
                commands.Add($"advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow "
                             + $"protocol=UDP localport={udpPorts}");
            }

            return commands;
        }

        private static string PortText(Need need) =>
            need.First == need.Last
                ? need.First.ToString(CultureInfo.InvariantCulture)
                : need.First.ToString(CultureInfo.InvariantCulture) + "-"
                  + need.Last.ToString(CultureInfo.InvariantCulture);

        /// <summary>路径里有引号 / 命令分隔符的话不拼进命令 (正常的安装路径不会有)。</summary>
        private static bool IsSafeForCommand(string path) =>
            !string.IsNullOrEmpty(path) && path.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%' }) < 0;
    }
}
