using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DIE.Server.Net;

/// <summary>
/// 服主填的、给登陆器服务器列表看的信息: 名字、位置、要不要上列表。
///
/// 文件是服务端旁边的 <c>server-info.txt</c> (服务器包里带一份模板), 一行一个 <c>键=值</c>:
///
///     name=阿强的僵尸岛
///     location=华东 · 杭州
///     listed=yes
///
/// 位置不填的话, 登记处按服务器的 IP 自动查一个大致地区 (见 <see cref="ServerRegistry"/>)。
/// <c>listed=no</c> 就不去登记处报到 —— 只给熟人玩的服务器用, 玩家照旧可以用 launcher.json 连。
/// </summary>
public sealed class ServerInfo
{
    public const string FileName = "server-info.txt";

    /// <summary>名字和位置最多这么长 —— 登陆器一行放得下, 也防有人塞一大段进来。</summary>
    public const int MaxLength = 40;

    public string Name { get; init; } = string.Empty;

    public string Location { get; init; } = string.Empty;

    public bool Listed { get; init; } = true;

    /// <summary>读 <paramref name="directory"/> 下的 server-info.txt; 没有这个文件就是全默认 (上列表、名字位置留空)。</summary>
    public static ServerInfo Load(string directory)
    {
        string path = Path.Combine(directory ?? string.Empty, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
    }

    public static ServerInfo Parse(string text)
    {
        string name = string.Empty;
        string location = string.Empty;
        bool listed = true;

        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith("#") || equals <= 0)
            {
                continue;
            }

            string key = line.Substring(0, equals).Trim().ToLowerInvariant();
            string value = line.Substring(equals + 1).Trim();

            switch (key)
            {
                case "name":
                    name = Clean(value);
                    break;
                case "location":
                    location = Clean(value);
                    break;
                case "listed":
                    listed = !(value.Equals("no", StringComparison.OrdinalIgnoreCase)
                               || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                               || value == "0");
                    break;
            }
        }

        return new ServerInfo { Name = name, Location = location, Listed = listed };
    }

    /// <summary>
    /// 去掉控制字符、截到 <see cref="MaxLength"/> 个字。别的服务器报上来的名字也过这一道 ——
    /// 那是别人写的字, 登记处原样转给每个玩家的登陆器。
    /// </summary>
    public static string Clean(string value)
    {
        string text = new string((value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= MaxLength ? text : text.Substring(0, MaxLength).Trim();
    }
}
