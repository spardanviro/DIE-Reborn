using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DIE.Server.Net;

/// <summary>
/// 定时去登记处报到, 好让这台服务器出现在每个玩家登陆器的服务器列表里 (见 <see cref="ServerRegistry"/>)。
///
/// 报到只是一个 GET: <c>http://登记处:8555/announce?port=本机状态接口端口</c>。登记处会反过来
/// 连本机的状态接口核验, 所以本机的状态端口必须对公网开放 —— 没开的话登记处会拒收,
/// 原因打在日志里。
///
/// 日志只在状态变化时打 (第一次登记上、从好变坏、从坏变好), 不会每分钟刷一行。
/// </summary>
public sealed class ServerAnnouncer : IDisposable
{
    /// <summary>默认的登记处: DIE Reborn 自己的服务器。环境变量 <c>DIE_REGISTRY</c> 可以换, 设成 off 就不报到。</summary>
    public const string DefaultRegistry = "47.99.74.220";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);

    private readonly string _url;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private string _lastOutcome;

    public ServerAnnouncer(string registryHost, int registryPort, int myStatusPort,
                           Func<string, CancellationToken, Task<string>> fetch = null)
    {
        _url = $"http://{registryHost}:{registryPort}/announce?port={myStatusPort}";
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _fetch = fetch ?? (async (url, cancel) => await _http.GetStringAsync(url, cancel));
    }

    public void Start() => _ = Loop();

    /// <summary>报到一次。返回登记处的回答 ("ok" 或拒收原因); 连不上返回 null。</summary>
    public async Task<string> AnnounceOnceAsync(CancellationToken cancel = default)
    {
        string outcome;
        try
        {
            outcome = (await _fetch(_url, cancel)).Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            outcome = null;
        }

        Report(outcome);
        return outcome;
    }

    private async Task Loop()
    {
        try
        {
            await Task.Delay(FirstDelay, _stop.Token);

            while (!_stop.IsCancellationRequested)
            {
                await AnnounceOnceAsync(_stop.Token);
                await Task.Delay(Interval, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Report(string outcome)
    {
        string state = outcome ?? "(连不上登记处)";
        if (state == _lastOutcome)
        {
            return;
        }

        _lastOutcome = state;

        if (outcome == "ok")
        {
            Log.Information("服务器列表: 已在登记处登记, 玩家的登陆器里能看到这台服务器");
        }
        else
        {
            Log.Warning("服务器列表: 登记没成功 —— {Why}。玩家仍可用 launcher.json 直连。", state);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}
