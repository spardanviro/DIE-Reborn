using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// 服务端旁边那两个服主自己填的文本文件:
    ///
    ///   - <c>server-info.txt</c>   名字 / 位置 / 上不上列表, 一行一个 <c>键=值</c>;
    ///   - <c>public-host.txt</c>   公网 IP 或域名, 第一行不是注释的就是。
    ///
    /// 改的时候只动那几行, 注释和别的行原样留着 (服主可能自己写了备注)。
    /// 规则和服务端 <c>ServerInfo</c> / <c>MatchHostAddress</c> 读它们的方式一致。
    /// </summary>
    public static class ServerFiles
    {
        public const string InfoFile = "server-info.txt";
        public const string HostFile = "public-host.txt";

        /// <summary>名字和位置的上限, 和服务端 <c>ServerInfo.MaxLength</c> 一样。</summary>
        public const int MaxLength = 40;

        public sealed class Info
        {
            public string Name { get; set; } = string.Empty;
            public string Location { get; set; } = string.Empty;
            public bool Listed { get; set; } = true;
        }

        public static Info ReadInfo(string serverDir)
        {
            Info info = new Info();
            foreach (string raw in ReadLines(Path.Combine(serverDir, InfoFile)))
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (line.Length == 0 || line.StartsWith("#") || equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim().ToLowerInvariant();
                string value = line.Substring(equals + 1).Trim();

                if (key == "name")
                {
                    info.Name = value;
                }
                else if (key == "location")
                {
                    info.Location = value;
                }
                else if (key == "listed")
                {
                    info.Listed = !(value.Equals("no", StringComparison.OrdinalIgnoreCase)
                                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                                    || value == "0");
                }
            }

            return info;
        }

        public static void WriteInfo(string serverDir, Info info)
        {
            string path = Path.Combine(serverDir, InfoFile);
            List<string> lines = ReadLines(path).ToList();

            Set(lines, "name", Clean(info.Name));
            Set(lines, "location", Clean(info.Location));
            Set(lines, "listed", info.Listed ? "yes" : "no");

            WriteLines(path, lines);
        }

        /// <summary>public-host.txt 里的地址; 没有返回空串。</summary>
        public static string ReadHost(string serverDir)
        {
            return ReadLines(Path.Combine(serverDir, HostFile))
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("#")) ?? string.Empty;
        }

        public static void WriteHost(string serverDir, string host)
        {
            string path = Path.Combine(serverDir, HostFile);
            List<string> lines = ReadLines(path).ToList();
            string value = (host ?? string.Empty).Trim();

            int at = lines.FindIndex(line => line.Trim().Length > 0 && !line.Trim().StartsWith("#"));
            if (at >= 0)
            {
                lines[at] = value;
            }
            else
            {
                if (lines.Count == 0)
                {
                    lines.Add("# 服务器公网地址 —— 发给从公网连进来的玩家, 他们的游戏拿它去连战斗服。");
                }

                lines.Add(value);
            }

            WriteLines(path, lines);
        }

        private static readonly Regex HostPattern = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?$");

        /// <summary>像不像一个 IP / 域名 (空也算合法 = 不填)。</summary>
        public static bool IsValidHost(string host)
        {
            string value = (host ?? string.Empty).Trim();
            return value.Length == 0 || HostPattern.IsMatch(value);
        }

        /// <summary>去掉控制字符、截到 <see cref="MaxLength"/> 个字 —— 和服务端 <c>ServerInfo.Clean</c> 一样。</summary>
        public static string Clean(string value)
        {
            string text = new string((value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
            return text.Length <= MaxLength ? text : text.Substring(0, MaxLength).Trim();
        }

        private static void Set(List<string> lines, string key, string value)
        {
            int at = lines.FindIndex(line =>
            {
                string trimmed = line.Trim();
                int equals = trimmed.IndexOf('=');
                return !trimmed.StartsWith("#") && equals > 0
                       && trimmed.Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
            });

            string text = key + "=" + value;
            if (at >= 0)
            {
                lines[at] = text;
            }
            else
            {
                lines.Add(text);
            }
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            string text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            if (text.EndsWith("\n"))
            {
                text = text.Substring(0, text.Length - 1);
            }

            return text.Split('\n');
        }

        private static void WriteLines(string path, IEnumerable<string> lines)
        {
            // 先写临时文件再换名: 服务端可能正好在读
            string temp = path + ".tmp";
            File.WriteAllText(temp, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
