using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DIE.Server.Net;

/// <summary>
/// 服务器控制台用的管理接口 (HTTP, **只听 127.0.0.1**, 端口由系统挑)。
///
///     GET /admin/overview   全貌: 三层人数、每一局、每个玩家 (见 <see cref="ServerOverview"/>)
///     GET /admin/reload     重读 server-info.txt (改名字 / 位置不用重启)
///     GET /admin/backup     在线备份账号库, 回备份文件的路径
///     GET /admin/shutdown   正常关服 (等同在服务端窗口按 Ctrl+C)
///
/// ## 控制台怎么找到它
///
/// 起来后把端口和一次性口令写进服务端旁边的 <see cref="FileName"/>, 关服时删掉。控制台读这个
/// 文件, 每个请求带上 <c>X-DIE-Admin: 口令</c> 请求头。
///
/// ## 为什么只听本机还要口令
///
/// 本机浏览器里打开的任何网页都能向 127.0.0.1 发 GET (比如一张 src 指向
/// <c>/admin/shutdown</c> 的图片)。网页加不了自定义请求头 (跨域要预检, 这里不应答),
/// 所以带口令请求头的只能是读得到 admin.json 的本机程序。
/// </summary>
public sealed class AdminServer : IDisposable
{
    public const string FileName = "admin.json";
    public const string TokenHeader = "X-DIE-Admin";

    private readonly Func<object> _overview;
    private readonly Func<string> _backup;
    private readonly Func<string> _reload;
    private readonly Action _shutdown;
    private readonly string _directory;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private TcpListener _listener;

    /// <param name="overview">现拼一份全貌 (序列化成 JSON 回给控制台)。</param>
    /// <param name="backup">备份账号库, 返回备份文件路径。</param>
    /// <param name="reload">重读 server-info.txt, 返回读到的名字 (日志 / 回显用)。</param>
    /// <param name="shutdown">正常关服。</param>
    /// <param name="directory">admin.json 写在哪 (服务端程序目录)。</param>
    public AdminServer(Func<object> overview, Func<string> backup, Func<string> reload, Action shutdown,
                       string directory)
    {
        _overview = overview;
        _backup = backup;
        _reload = reload;
        _shutdown = shutdown;
        _directory = directory;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    public string Token { get; }

    public int Port { get; private set; }

    public string FilePath => Path.Combine(_directory, FileName);

    public bool Start(int port = 0)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start(backlog: 8);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            Log.Warning(ex, "管理接口起不来 —— 控制台看不到详情, 不影响开服");
            return false;
        }

        try
        {
            WriteFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "写不了 {File} —— 控制台找不到管理接口", FilePath);
        }

        _ = AcceptLoop();
        Log.Information("管理接口已监听 127.0.0.1:{Port} (服务器控制台用, 只对本机)", Port);
        return true;
    }

    private void WriteFile()
    {
        string json = JsonSerializer.Serialize(new
        {
            port = Port,
            token = Token,
            pid = Environment.ProcessId,
        });

        // 先写临时文件再换名 —— 控制台不会读到写了一半的
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
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
                Log.Debug(ex, "管理接口 accept 出错");
                continue;
            }

            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                (string target, string token) = await ReadRequest(stream);
                if (target == null)
                {
                    await Reply(stream, 400, new { error = "bad request" });
                    return;
                }

                if (!TokenMatches(token))
                {
                    await Reply(stream, 403, new { error = "forbidden" });
                    return;
                }

                await Route(stream, target);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "管理接口处理请求出错");
            }
        }
    }

    private async Task Route(NetworkStream stream, string target)
    {
        int question = target.IndexOf('?');
        string path = question >= 0 ? target.Substring(0, question) : target;

        switch (path)
        {
            case "/admin/overview":
                await Reply(stream, 200, _overview());
                return;

            case "/admin/reload":
                await Reply(stream, 200, new { ok = true, name = _reload() });
                return;

            case "/admin/backup":
                try
                {
                    await Reply(stream, 200, new { ok = true, path = _backup() });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "备份账号库失败");
                    await Reply(stream, 500, new { ok = false, error = ex.Message });
                }

                return;

            case "/admin/shutdown":
                await Reply(stream, 200, new { ok = true });
                Log.Information("控制台要求关服");
                _shutdown();
                return;

            default:
                await Reply(stream, 404, new { error = "not found" });
                return;
        }
    }

    /// <summary>口令比较 (定长时间, 不给逐字猜的机会)。</summary>
    public bool TokenMatches(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        byte[] given = Encoding.ASCII.GetBytes(token.Trim());
        byte[] expected = Encoding.ASCII.GetBytes(Token);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>读到空行为止: 返回 GET 的目标和口令请求头; 不是合法的 GET 时目标为 null。</summary>
    private async Task<(string Target, string Token)> ReadRequest(NetworkStream stream)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        byte[] buffer = new byte[8192];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), timeout.Token);
            if (read <= 0)
            {
                return (null, null);
            }

            filled += read;
            string text = Encoding.ASCII.GetString(buffer, 0, filled);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            string[] lines = text.Substring(0, end).Split("\r\n");
            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2 || parts[0] != "GET")
            {
                return (null, null);
            }

            string token = null;
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim()
                        .Equals(TokenHeader, StringComparison.OrdinalIgnoreCase))
                {
                    token = line.Substring(colon + 1).Trim();
                }
            }

            return (parts[1], token);
        }

        return (null, null);
    }

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task Reply(NetworkStream stream, int code, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json);
        string reason = code switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "Error",
        };

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {bytes.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n"));
        await stream.WriteAsync(bytes);
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

        try
        {
            // 只删自己写的那份 —— 万一同一目录又起了一个服务端, 别把它的删了
            if (File.Exists(FilePath) && File.ReadAllText(FilePath).Contains(Token, StringComparison.Ordinal))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
