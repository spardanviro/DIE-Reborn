using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DIE.Server.Net;

/// <summary>
/// 服务器列表的登记处: 各地开的服务器定时来报到, 登陆器从这里拿列表。
///
/// ## 谁是登记处
///
/// 默认是 DIE Reborn 自己那台服务器 (玩家 2026-10-14 定: "用你的云服务器")。哪台服务端发现
/// 自己的公网地址就是登记处地址, 就兼做登记处 —— 见 Program 里的接线。它自己不用报到,
/// 列表里永远排第一个。
///
/// ## 报到要过反向核验
///
/// 报到只说"我在哪个端口", **地址取这条连接的来源 IP**, 不信自报 —— 不然谁都能把别人的
/// IP 登记上来。登记处随即反过来连 <c>http://来源IP:端口/status</c>, 能拿到一份 DIE 服务端的
/// 状态 (<c>"open": true</c>) 才收。所以列表里的每一项都是"此刻从公网连得上"的服务器,
/// 内网地址、端口没开的、不是 DIE 服务端的, 一概进不来。
///
/// 每次报到都重新核验一遍, 人数之类顺便更新; <see cref="Expiry"/> 内没再报到的从列表里消失。
///
/// ## 位置
///
/// 服主在 server-info.txt 里填了就用填的。没填的, 按服务器 IP 查一次大致地区
/// (ip-api.com, 免费、不用注册; 只查服务器的 IP, 不查玩家的), 中英文各一份, 缓存一天。
/// 查不到就空着。玩家 2026-10-14 定。
/// </summary>
public sealed class ServerRegistry : IDisposable
{
    /// <summary>这么久没报到就从列表里拿掉。报到间隔见 <see cref="ServerAnnouncer.Interval"/>。</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(3);

    /// <summary>列表最多收这么多台 —— 防有人批量刷。</summary>
    public const int MaxServers = 200;

    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GeoCacheFor = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<string, Entry> _servers = new();
    private readonly ConcurrentDictionary<IPAddress, (DateTime At, Place Place)> _geo = new();
    private readonly Func<Entry> _self;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly Func<IPAddress, CancellationToken, Task<Place>> _locate;
    private readonly HttpClient _http;

    /// <summary>一台服务器在列表里的样子。</summary>
    /// <param name="Host">玩家连它用的地址。</param>
    /// <param name="StatusPort">它的状态接口端口 (登陆器拿来问人数)。</param>
    /// <param name="Location">中文位置 (服主填的, 或者查出来的)。</param>
    /// <param name="LocationEn">英文位置 (服主填的就和中文那份一样)。</param>
    public sealed record Entry(string Host, int StatusPort, string Name, string Location, string LocationEn,
                               int Online, int InMatch, int Matches, string Version, DateTime SeenUtc);

    /// <summary>查出来的地区, 中英文各一份。</summary>
    public sealed record Place(string Zh, string En)
    {
        public static readonly Place Unknown = new Place(string.Empty, string.Empty);
    }

    /// <param name="self">登记处自己那一项 (每次列表都现取, 人数是实时的)。</param>
    /// <param name="fetch">发 HTTP GET 拿文本。测试注入假的; 默认用不走代理的 HttpClient。</param>
    /// <param name="locate">按 IP 查地区。测试注入假的; 默认问 ip-api.com。</param>
    public ServerRegistry(Func<Entry> self,
                          Func<string, CancellationToken, Task<string>> fetch = null,
                          Func<IPAddress, CancellationToken, Task<Place>> locate = null)
    {
        _self = self;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = VerifyTimeout })
        {
            Timeout = VerifyTimeout,
        };
        _fetch = fetch ?? (async (url, cancel) => await _http.GetStringAsync(url, cancel));
        _locate = locate ?? AskIpApi;
    }

    /// <summary>测试用: 允许内网 / 本机地址报到 (正式环境那些地址别人连不上, 收了也没用)。</summary>
    public bool AllowPrivateAddresses { get; init; }

    public int Count => _servers.Count;

    /// <summary>
    /// 一台服务器来报到。核验通过就收进 (或刷新) 列表。
    /// </summary>
    /// <returns>收了就是 null; 不收返回原因 (回给报到的那台, 它会打进日志)。</returns>
    public async Task<string> AnnounceAsync(IPAddress from, int statusPort, DateTime nowUtc,
                                            CancellationToken cancel = default)
    {
        if (from.IsIPv4MappedToIPv6)
        {
            from = from.MapToIPv4();
        }

        if (statusPort < 1 || statusPort > 65535)
        {
            return "端口不对";
        }

        if (!AllowPrivateAddresses && !IsPublic(from))
        {
            return "来源是内网地址, 别人连不上";
        }

        string host = from.ToString();
        string key = host + ":" + statusPort;

        if (!_servers.ContainsKey(key) && _servers.Count >= MaxServers)
        {
            return "列表满了";
        }

        string json;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(VerifyTimeout);
            json = await _fetch($"http://{FormatHost(from)}:{statusPort}/status", timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException)
        {
            _servers.TryRemove(key, out _);
            return $"反向连不上 {host}:{statusPort} —— 状态接口端口没对公网开放?";
        }

        Entry entry = ParseStatus(json, host, statusPort, nowUtc);
        if (entry == null)
        {
            _servers.TryRemove(key, out _);
            return "那个端口上不是 DIE 服务端";
        }

        if (entry.Location.Length == 0)
        {
            Place place = await LocateAsync(from, nowUtc, cancel);
            entry = entry with { Location = place.Zh, LocationEn = place.En };
        }

        bool fresh = !_servers.ContainsKey(key);
        _servers[key] = entry;

        if (fresh)
        {
            Log.Information("服务器列表: 新登记 {Host}:{Port} \"{Name}\" ({Location})",
                host, statusPort, entry.Name, entry.Location);
        }

        return null;
    }

    /// <summary>当前列表: 登记处自己排第一, 其余按在线人数从多到少。过期的顺手清掉。</summary>
    public IReadOnlyList<Entry> List(DateTime nowUtc)
    {
        foreach (KeyValuePair<string, Entry> server in _servers)
        {
            if (nowUtc - server.Value.SeenUtc > Expiry)
            {
                _servers.TryRemove(server.Key, out _);
            }
        }

        List<Entry> list = new List<Entry>();

        Entry self = _self?.Invoke();
        if (self != null)
        {
            if (self.Location.Length == 0 && IPAddress.TryParse(self.Host, out IPAddress selfIp)
                && _geo.TryGetValue(selfIp, out var known))
            {
                self = self with { Location = known.Place.Zh, LocationEn = known.Place.En };
            }

            list.Add(self);
        }

        list.AddRange(_servers.Values
            .Where(server => self == null || server.Host != self.Host || server.StatusPort != self.StatusPort)
            .OrderByDescending(server => server.Online)
            .ThenBy(server => server.Name, StringComparer.Ordinal));

        return list;
    }

    /// <summary>登记处自己没填位置时, 开服时查一次 (之后从缓存出)。</summary>
    public async Task LocateSelfAsync(string host, CancellationToken cancel = default)
    {
        if (IPAddress.TryParse(host, out IPAddress ip) && IsPublic(ip))
        {
            await LocateAsync(ip, DateTime.UtcNow, cancel);
        }
    }

    public byte[] ListJson(DateTime nowUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["v"] = 1,
            ["servers"] = List(nowUtc).Select(server => new Dictionary<string, object>
            {
                ["host"] = server.Host,
                ["statusPort"] = server.StatusPort,
                ["name"] = server.Name,
                ["location"] = server.Location,
                ["locationEn"] = server.LocationEn,
                ["online"] = server.Online,
                ["inMatch"] = server.InMatch,
                ["matches"] = server.Matches,
                ["version"] = server.Version,
            }).ToList(),
        });

    /// <summary>
    /// 别的服务器的 /status 回答 -&gt; 一项。不是 DIE 服务端的回答 (没有 <c>"open": true</c>) 返回 null。
    /// 名字和位置是别人写的字, 过一遍 <see cref="ServerInfo.Clean"/>。
    /// </summary>
    public static Entry ParseStatus(string json, string host, int statusPort, DateTime nowUtc)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("open", out JsonElement open) || open.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            string location = ServerInfo.Clean(Text(root, "location"));

            return new Entry(host, statusPort,
                ServerInfo.Clean(Text(root, "name")),
                location, location,
                Number(root, "online"), Number(root, "inMatch"), Number(root, "matches"),
                ServerInfo.Clean(Text(root, "version")),
                nowUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Place> LocateAsync(IPAddress ip, DateTime nowUtc, CancellationToken cancel)
    {
        if (_geo.TryGetValue(ip, out var cached) && nowUtc - cached.At < GeoCacheFor)
        {
            return cached.Place;
        }

        Place place;
        try
        {
            place = await _locate(ip, cancel) ?? Place.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            place = Place.Unknown;
        }

        _geo[ip] = (nowUtc, place);
        return place;
    }

    /// <summary>ip-api.com 的免费接口 (每分钟 45 次以内, 不用注册)。中英文各问一次。</summary>
    private async Task<Place> AskIpApi(IPAddress ip, CancellationToken cancel)
    {
        string zh = Describe(await _fetch($"http://ip-api.com/json/{ip}?fields=status,country,regionName,city&lang=zh-CN",
            cancel));
        string en = Describe(await _fetch($"http://ip-api.com/json/{ip}?fields=status,country,regionName,city&lang=en",
            cancel));
        return new Place(zh, en.Length > 0 ? en : zh);
    }

    /// <summary>ip-api 的回答 -&gt; "中国 · 浙江 · 杭州" (重复的层级合掉, 比如直辖市)。</summary>
    public static string Describe(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (Text(root, "status") != "success")
            {
                return string.Empty;
            }

            List<string> parts = new List<string>();
            foreach (string part in new[] { Text(root, "country"), Text(root, "regionName"), Text(root, "city") })
            {
                string clean = ServerInfo.Clean(part);
                if (clean.Length > 0 && !parts.Contains(clean))
                {
                    parts.Add(clean);
                }
            }

            return ServerInfo.Clean(string.Join(" · ", parts));
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>公网可达的地址: 不是本机、内网、链路本地、组播。</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
        {
            return false;
        }

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte first = ip.GetAddressBytes()[0];
            return (first & 0xFE) != 0xFC;       // fc00::/7 唯一本地
        }

        byte[] b = ip.GetAddressBytes();
        return !(b[0] == 10
                 || b[0] == 0
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)       // 运营商级 NAT
                 || b[0] >= 224);
    }

    private static string FormatHost(IPAddress ip) =>
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number)
            ? Math.Max(0, number)
            : 0;

    public void Dispose() => _http.Dispose();
}
