using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 服务端进程: 找到正在跑的、起一个新的、正常关掉。
    ///
    /// ## 服务端在后台跑, 不开黑窗口
    ///
    /// 控制台起服务端时不给它开控制台窗口 (CreateNoWindow), 日志从 logs\ 里的文件读
    /// (见 <see cref="LogTail"/>)。所以**关掉控制台不会关服** —— 再开控制台会自动接上
    /// 正在跑的那个。用「启动服务器.cmd」开的服务端, 控制台同样接得上。
    ///
    /// ## 关服走管理接口
    ///
    /// 先请服务端自己退出 (<c>/admin/shutdown</c>, 和在它窗口里按 Ctrl+C 一样: 停监听、
    /// 写完日志、关库), 等它 <see cref="GracefulWait"/>; 不理 (卡死了 / 管理接口没开) 才强行结束。
    /// </summary>
    internal sealed class ServerProcess
    {
        public const string ExeName = "DIE.Server.exe";

        private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(20);

        public ServerProcess(string directory)
        {
            Directory = directory;
        }

        public string Directory { get; }

        public string ExePath => Path.Combine(Directory, ExeName);

        public bool ExeExists => File.Exists(ExePath);

        /// <summary>控制台正在看着的那个进程 (自己起的, 或者接上的)。</summary>
        public Process Current { get; private set; }

        public bool IsRunning
        {
            get
            {
                try
                {
                    return Current != null && !Current.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        /// <summary>找这个目录里的服务端是不是已经在跑 (先看 admin.json 里的进程号, 再按路径找)。</summary>
        public Process Attach()
        {
            if (IsRunning)
            {
                return Current;
            }

            AdminEndpoint endpoint = AdminClient.ReadEndpoint(Directory);
            if (endpoint != null && IsOurs(TryGet(endpoint.Pid)))
            {
                return Current = TryGet(endpoint.Pid);
            }

            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                if (IsOurs(process))
                {
                    return Current = process;
                }

                process.Dispose();
            }

            return null;
        }

        public Process Start()
        {
            ProcessStartInfo start = new ProcessStartInfo(ExePath)
            {
                WorkingDirectory = Directory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // 留着的旧 admin.json (上次没正常关) 会让控制台连错端口: 起之前删掉, 新的由服务端起来后写
            try
            {
                File.Delete(Path.Combine(Directory, AdminClient.EndpointFile));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Current = Process.Start(start);
            return Current;
        }

        /// <summary>
        /// 正常关服; <paramref name="askToQuit"/> 是"请它自己退出"那一下 (管理接口), 失败也继续 ——
        /// 等不到就强行结束。返回 true = 它自己退的, false = 强行结束的。
        /// </summary>
        public async Task<bool> StopAsync(Func<Task> askToQuit)
        {
            Process process = Current;
            if (process == null || process.HasExited)
            {
                return true;
            }

            try
            {
                await askToQuit().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 管理接口不通 —— 下面等不到就强关
            }

            if (await WaitForExitAsync(process, GracefulWait).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                process.Kill();
                await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            return false;
        }

        private static Task<bool> WaitForExitAsync(Process process, TimeSpan timeout) =>
            Task.Run(() =>
            {
                try
                {
                    return process.WaitForExit((int)timeout.TotalMilliseconds);
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            });

        private bool IsOurs(Process process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                return !process.HasExited && string.Equals(Path.GetFullPath(process.MainModule.FileName),
                    Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException
                                           || ex is NotSupportedException)
            {
                return false;    // 别的用户的进程 / 刚退出
            }
        }

        private static Process TryGet(int pid)
        {
            try
            {
                return pid > 0 ? Process.GetProcessById(pid) : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
