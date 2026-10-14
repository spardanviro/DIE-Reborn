using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 读 Windows 防火墙的规则 (HNetCfg.FwPolicy2, 普通权限就能读), 以及提权跑 netsh 修规则。
    /// 判断"够不够"在 <see cref="FirewallPlan"/> 里。
    /// </summary>
    internal static class Firewall
    {
        private const int DirectionIn = 1;
        private const int ActionAllow = 1;

        /// <summary>读规则 + 当前网络类型下防火墙开没开。读不了 (组策略锁了之类) 抛异常。</summary>
        public static (List<FirewallPlan.Rule> Rules, bool On) Read()
        {
            Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true);
            object policy = Activator.CreateInstance(type);

            try
            {
                int profiles = Convert.ToInt32(Get(policy, "CurrentProfileTypes"));
                bool on = false;
                foreach (int profile in new[] { 1, 2, 4 })     // 域 / 专用 / 公用
                {
                    if ((profiles & profile) != 0 && Convert.ToBoolean(Get(policy, "FirewallEnabled", profile)))
                    {
                        on = true;
                    }
                }

                List<FirewallPlan.Rule> rules = new List<FirewallPlan.Rule>();
                object all = Get(policy, "Rules");
                try
                {
                    foreach (object rule in (IEnumerable)all)
                    {
                        try
                        {
                            rules.Add(new FirewallPlan.Rule
                            {
                                Name = Get(rule, "Name") as string,
                                Enabled = Convert.ToBoolean(Get(rule, "Enabled")),
                                Inbound = Convert.ToInt32(Get(rule, "Direction")) == DirectionIn,
                                Allow = Convert.ToInt32(Get(rule, "Action")) == ActionAllow,
                                Protocol = Convert.ToInt32(Get(rule, "Protocol")),
                                LocalPorts = Get(rule, "LocalPorts") as string,
                                Application = Get(rule, "ApplicationName") as string,
                            });
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(rule);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(all);
                }

                return (rules, on);
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }

        private static object Get(object target, string name, params object[] args) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);

        /// <summary>
        /// 以管理员身份跑这些 netsh 命令 (弹一次 UAC)。用户点了"否"返回 false。
        /// </summary>
        public static bool RunElevated(IReadOnlyList<string> netshArguments)
        {
            if (netshArguments.Count == 0)
            {
                return true;
            }

            string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
            string script = string.Join(" & ", netshArguments.Select(args => "\"" + netsh + "\" " + args));

            ProcessStartInfo start = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                "/s /c \"" + script + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using (Process process = Process.Start(start))
                {
                    process?.WaitForExit(60000);
                }

                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return false;    // UAC 点了"否"
            }
        }
    }
}
