using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// 和服务端打交道的两条 HTTP 路:
    ///
    ///   - 本机的管理接口 (地址和口令在服务端旁边的 admin.json 里, 见服务端 <c>AdminServer</c>);
    ///   - 登记处的 <c>/probe</c> —— 从外面反过来连本机的端口, 看外网进不进得来。
    ///
    /// 都不走代理: 本机地址本来就不该走; 探测要的正是"从公网直连"的结果。
    /// </summary>
    public sealed class AdminClient : IDisposable
    {
        public const string EndpointFile = "admin.json";
        public const string TokenHeader = "X-DIE-Admin";

        private readonly HttpClient _http;

        public AdminClient()
        {
            _http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
        }

        /// <summary>读服务端目录里的 admin.json; 没有 / 读不懂返回 null (服务端没开, 或者是不带管理接口的旧版)。</summary>
        public static AdminEndpoint ReadEndpoint(string serverDir)
        {
            try
            {
                string path = Path.Combine(serverDir ?? string.Empty, EndpointFile);
                if (!File.Exists(path))
                {
                    return null;
                }

                AdminEndpoint endpoint = Json.Read<AdminEndpoint>(File.ReadAllText(path, Encoding.UTF8));
                return endpoint != null && endpoint.IsValid ? endpoint : null;
            }
            catch (IOException)
            {
                return null;     // 服务端正在写 —— 下一轮再读
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        public async Task<Overview> OverviewAsync(AdminEndpoint endpoint, CancellationToken cancel)
        {
            return Overview.Parse(await GetAsync(endpoint, "/admin/overview", cancel).ConfigureAwait(false));
        }

        /// <summary>管理接口的一个 GET; 不是 200 抛 <see cref="HttpRequestException"/>。</summary>
        public async Task<string> GetAsync(AdminEndpoint endpoint, string path, CancellationToken cancel)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
                       "http://127.0.0.1:" + endpoint.Port + path))
            {
                request.Headers.Add(TokenHeader, endpoint.Token);

                using (HttpResponseMessage response = await _http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException((int)response.StatusCode + " " + body);
                    }

                    return body;
                }
            }
        }

        /// <summary>
        /// 请登记处反过来连本机: 回答里有外面看到的公网 IP 和每个 TCP 端口的结果。
        /// 登记处是旧版 (没有 /probe) 时返回 null。
        /// </summary>
        public async Task<ProbeResult> ProbeAsync(string registryHost, int registryPort, int statusPort,
                                                  CancellationToken cancel)
        {
            string url = "http://" + registryHost + ":" + registryPort + "/probe?status=" + statusPort;

            using (HttpResponseMessage response = await _http.GetAsync(url, cancel).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                ProbeResult result = Json.Read<ProbeResult>(
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return result?.Ports == null ? null : result;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
