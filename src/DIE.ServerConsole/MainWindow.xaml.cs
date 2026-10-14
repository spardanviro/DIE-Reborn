using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 服务器控制台的主窗口。这个文件: 启动流程、开服 / 停服、每 2 秒一轮的刷新。
    /// 其余几页在 MainWindow.*.cs 里 (概览渲染、日志、网络、设置)。
    ///
    /// 玩家 2026-10-14: "打开控制台先自动检查端口是否开放, 没有开放的话自动开放, 然后可以设置服务器名称,
    /// 开服停服, 可以查看服务器里的人数, 可以细化到三层服务器里各自的人数, 服务器负载等"。
    /// </summary>
    public partial class MainWindow : Window
    {
        private enum ServerState
        {
            Missing,        // 目录里没有 DIE.Server.exe
            Stopped,
            Starting,       // 进程起来了, 管理接口还没应答 (加载地图数据要十几秒)
            Running,
            Stopping,
        }

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        /// <summary>这么久里崩了这么多次就不再自动重开 (多半是配置问题, 重开也是白开)。</summary>
        private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
        private const int CrashLimit = 3;

        private readonly ConsoleSettings _settings;
        private readonly AdminClient _admin = new AdminClient();
        private readonly SystemLoad _load = new SystemLoad();
        private readonly DispatcherTimer _tick;
        private readonly List<DateTime> _crashes = new List<DateTime>();

        private ServerProcess _server;
        private ServerState _state = ServerState.Stopped;
        private Overview _overview;
        private DateTime _stateSince = DateTime.UtcNow;
        private bool _stopRequested;
        private bool _polling;
        private bool _adminSilent;
        private bool _probedThisRun;

        public MainWindow()
        {
            InitializeComponent();

            _settings = ConsoleSettings.Load(Platform.SettingsPath);
            _tick = new DispatcherTimer { Interval = PollInterval };
            _tick.Tick += async (_, __) => await PollAsync();

            ConsoleVersion.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Loaded += async (_, __) => await StartUpAsync();
            StateChanged += (_, __) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
        }

        // ==================================================================
        // 启动流程
        // ==================================================================

        private async Task StartUpAsync()
        {
            UseServerDir(ResolveServerDir());
            ApplyLanguage();
            LoadConsoleOptions();
            InitLog();

            if (_server.ExeExists && _server.Attach() != null)
            {
                SetState(ServerState.Starting);         // 接上了: 下一轮刷新问到管理接口就转"运行中"
                Say(Lang.T("msg.attached"));
            }
            else
            {
                SetState(_server.ExeExists ? ServerState.Stopped : ServerState.Missing);
            }

            _tick.Start();

            // 先查防火墙 (缺了自动补, 弹一次管理员确认), 再按设置自动开服
            await CheckFirewallAsync(autoFix: true);

            if (_state == ServerState.Stopped && _settings.AutoStart)
            {
                StartServer();
            }

            await PollAsync();
        }

        /// <summary>
        /// 服务端在哪: 控制台旁边有 DIE.Server.exe 就是它 (服务器包里两者放在一起, 拷到哪都对);
        /// 没有才用设置里记的那个目录。
        /// </summary>
        private string ResolveServerDir()
        {
            if (File.Exists(Path.Combine(Platform.ExeDir, ServerProcess.ExeName)))
            {
                return Platform.ExeDir;
            }

            if (!string.IsNullOrWhiteSpace(_settings.ServerDir)
                && File.Exists(Path.Combine(_settings.ServerDir, ServerProcess.ExeName)))
            {
                return _settings.ServerDir;
            }

            return Platform.ExeDir;
        }

        private void UseServerDir(string directory)
        {
            _server = new ServerProcess(directory);
            _tail = new LogTail(Path.Combine(directory, "logs"));
            ServerDirText.Text = directory;
            LoadServerFields();
        }

        // ==================================================================
        // 开服 / 停服
        // ==================================================================

        private void Start_Click(object sender, RoutedEventArgs e) => StartServer();

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (ConfirmStop())
            {
                await StopServerAsync();
            }
        }

        private async void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (ConfirmStop())
            {
                await StopServerAsync();
                StartServer();
            }
        }

        /// <summary>还有人在线时先问一句。</summary>
        private bool ConfirmStop()
        {
            int online = _overview?.Lobby.Online ?? 0;
            if (_state != ServerState.Running || online == 0)
            {
                return true;
            }

            return MessageBox.Show(this, Lang.T("dialog.stopWithPlayers", online), Lang.T("window.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void StartServer()
        {
            if (!_server.ExeExists)
            {
                SetState(ServerState.Missing);
                NavSettings.IsChecked = true;
                return;
            }

            if (_server.Attach() != null)
            {
                SetState(ServerState.Starting);
                return;
            }

            try
            {
                _stopRequested = false;
                _overview = null;
                _probedThisRun = false;
                _adminSilent = false;
                _server.Start();
                SetState(ServerState.Starting);
                Say(Lang.T("msg.starting"));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                SetState(ServerState.Stopped);
                Say(Lang.T("msg.startFailed", ex.Message));
            }
        }

        private async Task StopServerAsync()
        {
            if (!_server.IsRunning)
            {
                SetState(ServerState.Stopped);
                return;
            }

            _stopRequested = true;
            SetState(ServerState.Stopping);
            Say(Lang.T("msg.stopping"));

            AdminEndpoint endpoint = AdminClient.ReadEndpoint(_server.Directory);
            bool clean = await _server.StopAsync(async () =>
            {
                if (endpoint != null)
                {
                    await _admin.GetAsync(endpoint, "/admin/shutdown", CancellationToken.None);
                }
            });

            _overview = null;
            SetState(ServerState.Stopped);
            Say(clean ? Lang.T("msg.stopped") : Lang.T("msg.killed"));
            RenderOverview();
        }

        // ==================================================================
        // 每 2 秒一轮
        // ==================================================================

        private async Task PollAsync()
        {
            if (_polling)
            {
                return;
            }

            _polling = true;
            try
            {
                PollLog();
                await PollServerAsync();
                SampleLoad();
                await MaybeBackUpAsync();
            }
            finally
            {
                _polling = false;
            }
        }

        private async Task PollServerAsync()
        {
            if (_state == ServerState.Stopping)
            {
                return;
            }

            if (!_server.IsRunning)
            {
                // 不是自己点的停服 -> 崩了 / 被关了
                if (_state == ServerState.Running || _state == ServerState.Starting)
                {
                    OnServerExited();
                }
                else if (_server.ExeExists && _server.Attach() != null)
                {
                    SetState(ServerState.Starting);      // 别处 (比如「启动服务器.cmd」) 开的
                }

                return;
            }

            AdminEndpoint endpoint = AdminClient.ReadEndpoint(_server.Directory);
            if (endpoint == null || endpoint.Pid != SafePid())
            {
                // 还在加载; 或者是不带管理接口的旧版服务端
                if (_state == ServerState.Starting && DateTime.UtcNow - _stateSince > TimeSpan.FromSeconds(90))
                {
                    SetState(ServerState.Running);
                    _adminSilent = true;
                    Say(Lang.T("msg.noAdmin"));
                }

                return;
            }

            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    _overview = await _admin.OverviewAsync(endpoint, timeout.Token);
                }

                _adminSilent = false;
                if (_state != ServerState.Running)
                {
                    SetState(ServerState.Running);
                    Say(Lang.T("msg.running"));
                    LoadServerFields();
                }

                RenderOverview();

                if (!_probedThisRun)
                {
                    _probedThisRun = true;
                    _ = ProbeAsync(quiet: true);    // 开服后自动测一次外网
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException
                                           || ex is OperationCanceledException)
            {
                if (_state == ServerState.Running && !_adminSilent)
                {
                    _adminSilent = true;
                    Say(Lang.T("msg.adminSilent"));
                }
            }
        }

        private int SafePid()
        {
            try
            {
                return _server.Current?.Id ?? 0;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        /// <summary>服务端自己退了 (不是控制台叫它停的): 记一笔, 按设置自动重开。</summary>
        private void OnServerExited()
        {
            int code = 0;
            try
            {
                code = _server.Current?.ExitCode ?? 0;
            }
            catch (InvalidOperationException)
            {
            }

            _overview = null;
            RenderOverview();

            if (_stopRequested)
            {
                SetState(ServerState.Stopped);
                return;
            }

            DateTime now = DateTime.UtcNow;
            _crashes.RemoveAll(at => now - at > CrashWindow);
            _crashes.Add(now);

            if (_settings.AutoRestart && _crashes.Count <= CrashLimit)
            {
                Say(Lang.T("msg.crashedRestarting", code));
                StartServer();
                return;
            }

            SetState(ServerState.Stopped);
            Say(_settings.AutoRestart ? Lang.T("msg.crashedGaveUp", code, CrashLimit) : Lang.T("msg.crashed", code));
            NavLog.IsChecked = true;     // 原因在日志里
        }

        private void SetState(ServerState state)
        {
            if (state != _state)
            {
                _state = state;
                _stateSince = DateTime.UtcNow;
            }

            RenderState();
        }

        private void RenderState()
        {
            bool running = _state == ServerState.Running;
            bool busy = _state == ServerState.Starting || _state == ServerState.Stopping;

            StartButton.IsEnabled = _state == ServerState.Stopped;
            StopButton.IsEnabled = running || _state == ServerState.Starting;
            RestartButton.IsEnabled = running;

            StateDot.Fill = running ? Palette.Alive
                : busy ? Palette.Hazard
                : _state == ServerState.Missing ? Palette.Dead
                : Palette.Faint;

            StateText.Text = Lang.T("state.server." + _state.ToString().ToLowerInvariant());

            if (running && _overview != null)
            {
                StateDetail.Text = Lang.T("header.detail", Format.Duration(_overview.UptimeSeconds),
                    string.IsNullOrEmpty(_overview.Version) ? Lang.T("header.devBuild") : _overview.Version);
            }
            else if (_state == ServerState.Missing)
            {
                StateDetail.Text = Lang.T("header.missing", _server.Directory);
            }
            else
            {
                StateDetail.Text = string.Empty;
            }

            HeaderName.Text = !string.IsNullOrEmpty(_overview?.Name) ? _overview.Name
                : !string.IsNullOrEmpty(NameBox.Text) ? NameBox.Text
                : Lang.T("header.unnamed");
        }

        /// <summary>底栏消息 (带时间, 最近一条)。</summary>
        private void Say(string message)
        {
            StatusBar.Text = DateTime.Now.ToString("HH:mm:ss") + "   " + message;
        }

        // ==================================================================
        // 窗口
        // ==================================================================

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageOverview == null)
            {
                return;      // InitializeComponent 途中
            }

            PageOverview.Visibility = Show(NavOverview);
            PageMatches.Visibility = Show(NavMatches);
            PagePlayers.Visibility = Show(NavPlayers);
            PageLog.Visibility = Show(NavLog);
            PageNetwork.Visibility = Show(NavNetwork);
            PageSettings.Visibility = Show(NavSettings);

            if (NavLog.IsChecked == true)
            {
                ScrollLogToEnd();
            }
        }

        private static Visibility Show(System.Windows.Controls.RadioButton nav) =>
            nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            Lang.Use(Lang.Other);
            _settings.Language = Lang.Current;
            SaveConsoleSettings();
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Title = Lang.T("window.title");
            LanguageLink.Content = Lang.T("lang.switch");
            CopySteamIdMenu.Header = Lang.T("players.copySteam");
            OpenProfileMenu.Header = Lang.T("players.openProfile");
            Loc.Refresh(this);

            RenderState();
            RenderOverview();
            RenderFirewall();
            RenderProbe();
            RenderCloudRules();
            RefreshLogFilter();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 关窗口时服务端还开着: 问停服再退出, 还是让它在后台接着跑 (之后再开控制台会接上)。
        /// </summary>
        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_closingConfirmed || !_server.IsRunning)
            {
                base.OnClosing(e);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(this, Lang.T("dialog.closeWhileRunning"),
                Lang.T("window.title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                await StopServerAsync();
                _closingConfirmed = true;
                Close();
                return;
            }

            base.OnClosing(e);
        }

        private bool _closingConfirmed;

        protected override void OnClosed(EventArgs e)
        {
            _tick.Stop();
            _admin.Dispose();
            base.OnClosed(e);
        }
    }
}
