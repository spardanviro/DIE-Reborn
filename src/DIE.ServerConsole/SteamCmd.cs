using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 没装 Steam 的机器 (云服务器) 上拿 Steam 客户端库: 从 Valve 官方地址下 SteamCMD,
    /// 跑一次让它自我更新 (更新完它的目录里就有 steamclient64.dll / tier0_s64.dll / vstdlib_s64.dll),
    /// 再拷到服务端旁边。SteamCMD 放在 %LocalAppData%\DIE Reborn\steamcmd\, 只在服主点按钮时才做。
    /// </summary>
    internal static class SteamCmd
    {
        /// <summary>Valve 官方的 SteamCMD 下载地址 (developer.valvesoftware.com/wiki/SteamCMD)。</summary>
        public const string DownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

        private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(10);

        public static string Directory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIE Reborn", "steamcmd");

        /// <summary>下载 (没有的话)、跑一次更新、拷库。<paramref name="progress"/> 报当前在做哪一步。</summary>
        public static async Task FetchClientLibrariesAsync(string serverDir, IProgress<string> progress,
                                                           CancellationToken cancel)
        {
            string dir = Directory;
            string exe = Path.Combine(dir, "steamcmd.exe");
            System.IO.Directory.CreateDirectory(dir);

            if (!File.Exists(exe))
            {
                progress.Report(Lang.T("steamcmd.downloading"));
                string zip = Path.Combine(dir, "steamcmd.zip");
                using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                using (Stream body = await http.GetStreamAsync(DownloadUrl).ConfigureAwait(false))
                using (FileStream file = File.Create(zip))
                {
                    await body.CopyToAsync(file, 81920, cancel).ConfigureAwait(false);
                }

                ZipFile.ExtractToDirectory(zip, dir);
                File.Delete(zip);
            }

            if (!GameFiles.CopySteamClient(dir, serverDir))
            {
                // 第一次跑 SteamCMD 会把自己更新完整 (几十 MB), 之后目录里才有那三个库
                progress.Report(Lang.T("steamcmd.updating"));
                await Task.Run(() => RunOnce(exe, dir), cancel).ConfigureAwait(false);

                if (!GameFiles.CopySteamClient(dir, serverDir))
                {
                    throw new FileNotFoundException(Lang.T("steamcmd.noLibraries", dir));
                }
            }
        }

        private static void RunOnce(string exe, string dir)
        {
            ProcessStartInfo start = new ProcessStartInfo(exe, "+quit")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (Process process = Process.Start(start))
            {
                if (process != null && !process.WaitForExit((int)UpdateTimeout.TotalMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    throw new TimeoutException(Lang.T("steamcmd.timeout"));
                }
            }
        }
    }
}
