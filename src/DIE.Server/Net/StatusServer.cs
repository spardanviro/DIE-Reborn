using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DIE.Server.Net;

/// <summary>
/// 给登陆器看的只读状态接口 (HTTP, 默认 TCP 8555)。
///
/// ## 三条路由, 全部只读
///
///     GET /status            {"online":3,"inMatch":2,"matches":1,"name":...,"location":...}
///     GET /patch/latest.json 补丁包镜像的版本和校验值 (发布脚本生成)
///     GET /patch/&lt;文件名&gt;     补丁包镜像本体 (服务器包里的 patch\ 文件夹)
///
/// 这台兼做服务器列表的登记处时 (<see cref="Registry"/> 不为空) 再多两条:
///
///     GET /servers           登陆器拿服务器列表
///     GET /announce?port=N   别的服务器来报到, 见 <see cref="ServerRegistry"/>
///
/// 补丁包的正主在 GitHub Releases; 这里只是国内直连 GitHub 下不动时的兜底
/// (玩家 2026-10-07 定)。登陆器不管从哪边下, 都拿校验值核对。
///
/// ## 为什么不用 HttpListener
///
/// HttpListener 走 http.sys, 监听非 localhost 的地址要么管理员权限要么先
/// <c>netsh http add urlacl</c> —— 服务器包是解压即用的, 不能多这一步。
/// 这里要的只是"读一行请求、回一段字节", 直接用 TcpListener 写。
///
/// ## 这个口是对公网开的
///
///   - 只认 GET, 请求头最多 4 KB、5 秒内收完, 否则断开;
///   - 文件名只允许字母数字和 <c>. _ -</c>, 而且只在 patch\ 这一层找 —— 没有路径可穿越;
///   - 每个 IP 10 秒内最多 <see cref="RequestsPerWindow"/> 次请求, 全局最多
///     <see cref="MaxConcurrent"/> 条连接同时在办 (下载大文件时占着)。
/// </summary>
public sealed class StatusServer : IDisposable
{
    public const int DefaultPort = 8555;

    /// <summary>补丁包镜像放在服务端程序旁边的这个文件夹里。</summary>
    public const string PatchFolder = "patch";

    public const int RequestsPerWindow = 30;
    public const int MaxConcurrent = 32;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly Regex SafeFileName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$");

    private readonly Func<Snapshot> _snapshot;
    private readonly string _patchDir;
    private readonly ConcurrentDictionary<IPAddress, (DateTime Start, int Count)> _hits = new();
    private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxConcurrent);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private TcpListener _listener;

    /// <summary>登陆器看到的那几个数。</summary>
    /// <param name="Online">登录着的账号数 (大厅连接, 局里的人也算)。</param>
    /// <param name="InMatch">正在局里的人数。</param>
    /// <param name="Matches">正在进行的局数。</param>
    /// <param name="MaxMatches">最多同时开几局。</param>
    /// <param name="Version">服务端版本 (服务器包的 version.txt, 开发机上为空)。</param>
    /// <param name="Name">服主起的名字 (server-info.txt)。</param>
    /// <param name="Location">服主填的位置 (server-info.txt)。</param>
    public sealed record Snapshot(int Online, int InMatch, int Matches, int MaxMatches, string Version,
                                  string Name = "", string Location = "");

    /// <summary>这台兼做服务器列表的登记处时设上; 平时为空, /servers 和 /announce 回 404。</summary>
    public ServerRegistry Registry { get; set; }

    public StatusServer(Func<Snapshot> snapshot, string baseDir)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _patchDir = Path.Combine(baseDir ?? AppContext.BaseDirectory, PatchFolder);
    }

    /// <summary>实际监听的端口 (传 0 让系统挑时用, 测试读它)。</summary>
    public int Port { get; private set; }

    public bool Start(IPAddress address, int port)
    {
        try
        {
            _listener = new TcpListener(address, port);
            _listener.Start(backlog: 64);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            Log.Error(ex, "状态接口起不来 (TCP {Port})", port);
            return false;
        }

        _ = AcceptLoop();
        Log.Information("状态接口已监听 TCP {Port} (登陆器用; 补丁镜像目录 {Dir}{Has})",
            Port, _patchDir, Directory.Exists(_patchDir) ? "" : " —— 目录不存在, 镜像未启用");
        return true;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "状态接口 accept 出错");
                continue;
            }

            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            // 满了就直接断 —— 排队只会让慢连接把口子占死
            if (!_slots.Wait(0))
            {
                return;
            }

            try
            {
                client.NoDelay = true;
                client.SendTimeout = 30000;
                NetworkStream stream = client.GetStream();
                IPAddress from = ((IPEndPoint)client.Client.RemoteEndPoint).Address;

                string target = await ReadRequestTarget(stream);
                if (target == null)
                {
                    await Reply(stream, 400, "text/plain", Encoding.UTF8.GetBytes("bad request"));
                    return;
                }

                if (!Allow(from, DateTime.UtcNow))
                {
                    await Reply(stream, 429, "text/plain", Encoding.UTF8.GetBytes("slow down"));
                    return;
                }

                await Route(stream, target, from);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or ObjectDisposedException)
            {
                // 对面断了 / 超时 —— 公网上天天有, 不值得记
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "状态接口处理请求出错");
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    /// <summary>读到空行为止, 返回 GET 的目标 (路径加查询串); 不是合法的 GET 返回 null。</summary>
    private async Task<string> ReadRequestTarget(NetworkStream stream)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        byte[] buffer = new byte[4096];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), timeout.Token);
            if (read <= 0)
            {
                return null;
            }

            filled += read;

            string text = Encoding.ASCII.GetString(buffer, 0, filled);
            if (!text.Contains("\r\n\r\n"))
            {
                continue;
            }

            string[] parts = text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal)).Split(' ');
            if (parts.Length < 2 || parts[0] != "GET")
            {
                return null;
            }

            return parts[1];
        }

        return null;
    }

    private async Task Route(NetworkStream stream, string target, IPAddress from)
    {
        int question = target.IndexOf('?');
        string path = question >= 0 ? target.Substring(0, question) : target;
        string query = question >= 0 ? target.Substring(question + 1) : string.Empty;

        if (path == "/status" || path == "/")
        {
            await Reply(stream, 200, "application/json; charset=utf-8", StatusJson());
            return;
        }

        if (Registry != null && path == "/servers")
        {
            await Reply(stream, 200, "application/json; charset=utf-8", Registry.ListJson(DateTime.UtcNow));
            return;
        }

        if (Registry != null && path == "/announce")
        {
            int port = int.TryParse(QueryValue(query, "port"), out int asked) ? asked : 0;
            string refused = await Registry.AnnounceAsync(from, port, DateTime.UtcNow, _stop.Token);
            await Reply(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(refused ?? "ok"));
            return;
        }

        const string prefix = "/patch/";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            string file = ResolvePatchFile(path.Substring(prefix.Length));
            if (file != null)
            {
                await ReplyFile(stream, file);
                return;
            }
        }

        await Reply(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
    }

    /// <summary>此刻的状态 (登记处把自己列进列表时用)。</summary>
    public Snapshot CurrentSnapshot() => _snapshot();

    private byte[] StatusJson()
    {
        Snapshot now = _snapshot();

        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["v"] = 1,
            ["open"] = true,
            ["online"] = now.Online,
            ["inMatch"] = now.InMatch,
            ["matches"] = now.Matches,
            ["maxMatches"] = now.MaxMatches,
            ["version"] = now.Version ?? string.Empty,
            ["name"] = now.Name ?? string.Empty,
            ["location"] = now.Location ?? string.Empty,
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        });
    }

    /// <summary>
    /// patch\ 里叫这个名字的文件的完整路径; 名字不合规 / 不存在返回 null。
    /// 只认"一层里的一个文件名" —— 斜杠、反斜杠、<c>..</c> 都过不了正则。
    /// </summary>
    public string ResolvePatchFile(string name)
    {
        if (string.IsNullOrEmpty(name) || !SafeFileName.IsMatch(name) || name.Contains(".."))
        {
            return null;
        }

        string full = Path.Combine(_patchDir, name);
        return File.Exists(full) ? full : null;
    }

    private static string QueryValue(string query, string key)
    {
        foreach (string pair in query.Split('&'))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0 && pair.Substring(0, equals) == key)
            {
                return Uri.UnescapeDataString(pair.Substring(equals + 1));
            }
        }

        return null;
    }

    /// <summary>这个 IP 这一下放不放行 (10 秒一个窗口)。</summary>
    public bool Allow(IPAddress from, DateTime nowUtc)
    {
        (DateTime Start, int Count) entry = _hits.AddOrUpdate(from,
            _ => (nowUtc, 1),
            (_, old) => nowUtc - old.Start > Window ? (nowUtc, 1) : (old.Start, old.Count + 1));

        // 表不能只进不出 —— 公网上扫端口的 IP 源源不断
        if (_hits.Count > 4096)
        {
            foreach (KeyValuePair<IPAddress, (DateTime Start, int Count)> hit in _hits)
            {
                if (nowUtc - hit.Value.Start > Window)
                {
                    _hits.TryRemove(hit.Key, out _);
                }
            }
        }

        return entry.Count <= RequestsPerWindow;
    }

    private static async Task Reply(NetworkStream stream, int code, string contentType, byte[] body)
    {
        await stream.WriteAsync(Header(code, contentType, body.Length));
        await stream.WriteAsync(body);
    }

    private static async Task ReplyFile(NetworkStream stream, string file)
    {
        using FileStream source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);

        string type = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "application/json; charset=utf-8"
            : "application/octet-stream";

        await stream.WriteAsync(Header(200, type, source.Length));
        await source.CopyToAsync(stream);
    }

    private static byte[] Header(int code, string contentType, long length)
    {
        string reason = code switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            429 => "Too Many Requests",
            _ => "Error",
        };

        return Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Connection: close\r\n\r\n");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }
    }
}
