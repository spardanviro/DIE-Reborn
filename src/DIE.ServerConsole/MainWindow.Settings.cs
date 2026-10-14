using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>设置页: 服务器名字 / 位置 / 上不上列表 / 公网地址, 控制台选项, 账号库备份, 服务端目录。</summary>
    public partial class MainWindow
    {
        private ServerFiles.Info _savedInfo = new ServerFiles.Info();
        private string _savedHost = string.Empty;
        private bool _backingUp;

        /// <summary>把服务端目录里的两个文件读进输入框。</summary>
        private void LoadServerFields()
        {
            _savedInfo = ServerFiles.ReadInfo(_server.Directory);
            _savedHost = ServerFiles.ReadHost(_server.Directory);

            NameBox.Text = _savedInfo.Name;
            LocationBox.Text = _savedInfo.Location;
            ListedBox.IsChecked = _savedInfo.Listed;
            HostBox.Text = _savedHost;
        }

        private void LoadConsoleOptions()
        {
            AutoStartBox.IsChecked = _settings.AutoStart;
            AutoRestartBox.IsChecked = _settings.AutoRestart;
            DailyBackupBox.IsChecked = _settings.DailyBackup;
            BackupHourBox.Text = _settings.BackupHour.ToString(CultureInfo.InvariantCulture);
            BackupNote.Text = string.IsNullOrEmpty(_settings.LastBackupDay)
                ? string.Empty
                : Lang.T("set.lastBackup", _settings.LastBackupDay);
        }

        private async void SaveServerSettings_Click(object sender, RoutedEventArgs e)
        {
            string host = (HostBox.Text ?? string.Empty).Trim();
            if (!ServerFiles.IsValidHost(host))
            {
                SaveNote.Text = Lang.T("set.badHost");
                SaveNote.Foreground = Palette.Dead;
                return;
            }

            ServerFiles.Info info = new ServerFiles.Info
            {
                Name = ServerFiles.Clean(NameBox.Text),
                Location = ServerFiles.Clean(LocationBox.Text),
                Listed = ListedBox.IsChecked == true,
            };

            try
            {
                ServerFiles.WriteInfo(_server.Directory, info);
                if (host != _savedHost)
                {
                    ServerFiles.WriteHost(_server.Directory, host);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SaveNote.Text = Lang.T("set.saveFailed", ex.Message);
                SaveNote.Foreground = Palette.Dead;
                return;
            }

            bool needsRestart = host != _savedHost || info.Listed != _savedInfo.Listed;
            _savedInfo = info;
            _savedHost = host;
            SaveNote.Foreground = Palette.Soft;

            // 名字和位置: 叫服务端重读, 马上生效 (登记处下一次来问状态时就是新名字)
            bool reloaded = false;
            AdminEndpoint endpoint = AdminClient.ReadEndpoint(_server.Directory);
            if (_state == ServerState.Running && endpoint != null)
            {
                try
                {
                    using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        await _admin.GetAsync(endpoint, "/admin/reload", timeout.Token);
                    }

                    reloaded = true;
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                                               || ex is TaskCanceledException || ex is OperationCanceledException)
                {
                }
            }

            SaveNote.Text = _state != ServerState.Running ? Lang.T("set.savedNextStart")
                : needsRestart ? Lang.T("set.savedRestart")
                : reloaded ? Lang.T("set.savedLive")
                : Lang.T("set.savedRestart");
            Say(SaveNote.Text);
            RenderState();
            RenderChecks();
        }

        private async void DetectHost_Click(object sender, RoutedEventArgs e)
        {
            SaveNote.Foreground = Palette.Soft;
            SaveNote.Text = Lang.T("set.detecting");

            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    ProbeResult result = await _admin.ProbeAsync(_settings.RegistryHost, ConsoleSettings.RegistryPort,
                        CurrentPorts().Status, timeout.Token);
                    if (result != null && !string.IsNullOrEmpty(result.Ip))
                    {
                        HostBox.Text = result.Ip;
                        SaveNote.Text = Lang.T("set.hostFilled");
                        if (_state == ServerState.Running)
                        {
                            _probe = result;
                            RenderProbe();
                            RenderChecks();
                        }

                        return;
                    }
                }

                SaveNote.Text = Lang.T("probe.unsupported");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException
                                           || ex is OperationCanceledException)
            {
                SaveNote.Text = Lang.T("probe.registryDown", _settings.RegistryHost);
            }
        }

        // ==================================================================
        // 控制台选项
        // ==================================================================

        private void ConsoleOption_Click(object sender, RoutedEventArgs e)
        {
            _settings.AutoStart = AutoStartBox.IsChecked == true;
            _settings.AutoRestart = AutoRestartBox.IsChecked == true;
            _settings.DailyBackup = DailyBackupBox.IsChecked == true;
            SaveConsoleSettings();
        }

        private void BackupHour_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(BackupHourBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour))
            {
                _settings.BackupHour = hour;
                SaveConsoleSettings();
            }

            BackupHourBox.Text = _settings.BackupHour.ToString(CultureInfo.InvariantCulture);
        }

        private void SaveConsoleSettings()
        {
            try
            {
                _settings.Save(Platform.SettingsPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Say(Lang.T("msg.settingsFailed", ex.Message));
            }
        }

        // ==================================================================
        // 账号库备份
        // ==================================================================

        private async void BackupNow_Click(object sender, RoutedEventArgs e) => await BackUpAsync(manual: true);

        /// <summary>每日自动备份: 开着服、到点了、今天还没备过。</summary>
        private async Task MaybeBackUpAsync()
        {
            if (_state == ServerState.Running && _settings.BackupDue(DateTime.Now))
            {
                await BackUpAsync(manual: false);
            }
        }

        private async Task BackUpAsync(bool manual)
        {
            if (_backingUp)
            {
                return;
            }

            AdminEndpoint endpoint = AdminClient.ReadEndpoint(_server.Directory);
            if (_state != ServerState.Running || endpoint == null)
            {
                if (manual)
                {
                    BackupNote.Text = Lang.T("set.backupNeedsServer");
                }

                return;
            }

            _backingUp = true;
            BackupButton.IsEnabled = false;
            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                {
                    await _admin.GetAsync(endpoint, "/admin/backup", timeout.Token);
                }

                _settings.LastBackupDay = DateTime.Now.ToString("yyyy-MM-dd");
                SaveConsoleSettings();
                BackupNote.Text = Lang.T("set.backupDone", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                Say(BackupNote.Text);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException
                                           || ex is OperationCanceledException)
            {
                BackupNote.Text = Lang.T("set.backupFailed", ex.Message);
                Say(BackupNote.Text);

                // 自动备份失败也记成"今天做过了", 免得每 2 秒重试一次刷屏; 手动的可以再点
                if (!manual)
                {
                    _settings.LastBackupDay = DateTime.Now.ToString("yyyy-MM-dd");
                    SaveConsoleSettings();
                }
            }
            finally
            {
                _backingUp = false;
                BackupButton.IsEnabled = true;
            }
        }

        private void OpenBackups_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(_server.Directory, "data", "backups");
            Platform.OpenFolder(Directory.Exists(dir) ? dir : _server.Directory);
        }

        // ==================================================================
        // 服务端目录
        // ==================================================================

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_server.IsRunning)
            {
                Say(Lang.T("msg.stopBeforeFolder"));
                return;
            }

            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("set.pickExe"),
                Filter = "DIE.Server.exe|DIE.Server.exe",
                InitialDirectory = Directory.Exists(_server.Directory) ? _server.Directory : null,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string dir = Path.GetDirectoryName(dialog.FileName);
            _settings.ServerDir = dir;
            SaveConsoleSettings();
            UseServerDir(dir);
            ResetLog();

            _firewall = null;
            _probe = null;
            SetState(_server.Attach() != null ? ServerState.Starting : ServerState.Stopped);
            _ = CheckFirewallAsync(autoFix: false);
            _ = PrepareFilesAsync();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e) => Platform.OpenFolder(_server.Directory);
    }
}
