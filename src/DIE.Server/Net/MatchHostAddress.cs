using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace DIE.Server.Net;

/// <summary>
/// 开局时下发给客户端的战斗服地址 —— 客户端拿到后拉起
/// <c>Dead Island Epidemic.exe +connect &lt;ip&gt; &lt;port&gt;</c>。
/// 必须是**这个客户端视角**能够着服务器的地址。
///
/// ## 为什么不能写死
///
/// 以前写死 127.0.0.1, 别人的游戏开局会去连**他自己电脑**。
///
/// ## 规则: 按客户端从哪来的分
///
/// | 客户端 | 服务器这一侧的本地地址 | 回什么 |
/// |---|---|---|
/// | 本机 | 127.0.0.1 | 127.0.0.1 |
/// | 局域网内 | 192.168.x.x | 它连进来用的那个 (就是本地地址) |
/// | 公网, 服务器网卡上就是公网 IP (VPS / 电脑直接拨号) | 公网 IP | 同上 |
/// | 公网, 服务器在路由器后面 (家用宽带 + 端口映射) | **内网 IP** | <see cref="PublicHostVariable"/> 配的公网地址 |
///
/// 最后一种是唯一需要配置的: NAT 后面服务器看不到自己的公网地址。其余情况
/// "客户端连进来用的地址"就是答案。按客户端分而不是一刀切, 是为了本机和局域网里的人
/// 不去绕公网地址 —— 很多家用路由器不支持从内网访问自己的公网 IP (NAT 回流)。
/// </summary>
public static class MatchHostAddress
{
    /// <summary>公网地址的环境变量名。优先于 <see cref="PublicHostFile"/>。</summary>
    public const string PublicHostVariable = "DIE_PUBLIC_HOST";

    /// <summary>
    /// 公网地址也可以写在这个文件里 (放在 run-server.cmd 旁边, 第一行非空非 # 的内容)。
    /// 比环境变量好改: 记事本打开就能改, 不用碰 GBK 编码的 run-server.cmd。
    /// </summary>
    public const string PublicHostFile = "public-host.txt";

    /// <summary>
    /// 读公网地址配置: 环境变量优先, 其次 <paramref name="directory"/> 下的
    /// <see cref="PublicHostFile"/>。都没有返回 null。
    /// </summary>
    public static string LoadPublicHost(string directory)
    {
        string fromEnv = Environment.GetEnvironmentVariable(PublicHostVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        string path = Path.Combine(directory, PublicHostFile);
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("#"));
    }

    /// <param name="local">匹配连接在服务端这一侧的本地端点。拿不到时传 null。</param>
    /// <param name="remote">客户端的端点。拿不到时传 null (当作本机/内网处理)。</param>
    /// <param name="publicHost">公网 IP 或域名; 空表示没配。</param>
    public static IPAddress Resolve(IPEndPoint local, IPEndPoint remote, string publicHost)
    {
        if (NeedsPublicHost(local, remote) && !string.IsNullOrWhiteSpace(publicHost))
        {
            IPAddress configured = ParseOrLookUp(publicHost.Trim());
            if (configured != null)
            {
                return configured;
            }
        }

        IPAddress address = Normalize(local?.Address);

        // 客户端只收 4 字节的 IPv4, 绑定用的 0.0.0.0 也不能发出去
        return address == null || address.Equals(IPAddress.Any) ? IPAddress.Loopback : address;
    }

    /// <summary>
    /// 这个客户端是不是从公网、隔着路由器进来的 —— 只有这种情况服务器自己的
    /// 地址对它没用, 得回配置的公网地址。
    /// </summary>
    public static bool NeedsPublicHost(IPEndPoint local, IPEndPoint remote)
    {
        IPAddress client = Normalize(remote?.Address);
        IPAddress server = Normalize(local?.Address);

        return client != null && !IsPrivate(client) && (server == null || IsPrivate(server));
    }

    /// <summary>公网地址配成了 IP 或域名, 解析成 IPv4。解析不了返回 null。</summary>
    public static IPAddress ParseOrLookUp(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress parsed))
        {
            return Normalize(parsed);
        }

        try
        {
            return Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>IPv4 映射的 IPv6 还原成 IPv4; 真正的 IPv6 返回 null (客户端只收 IPv4)。</summary>
    private static IPAddress Normalize(IPAddress address)
    {
        if (address == null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
    }

    /// <summary>
    /// 回环 / 私网 / 链路本地 / 运营商级 NAT (100.64/10, 也是 Tailscale 用的段)。
    /// 这些地址的客户端和服务器"在同一边", 直接回本地地址。
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 127
            || b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] == 0;
    }
}
