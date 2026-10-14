using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 开服前准备: 公开发布包里没有游戏数据和 Steam 客户端库 (见 <see cref="GameFiles"/>), 控制台打开时
    /// 先从本机的 Steam / 游戏自动补; 补不齐 (云服务器上没装游戏) 就在概览页顶上提示服主手动选。
    /// </summary>
    public partial class MainWindow
    {
        private GameFiles.Status _files = new GameFiles.Status { GameData = true, Crafting = true, SteamClient = true };
        private bool _preparing;

        private bool FilesReady => _files.Ready;

        /// <summary>控制台打开时: 缺什么就先从本机自动补, 补完再看还缺什么。</summary>
        private async Task PrepareFilesAsync()
        {
            _files = GameFiles.Check(_server.Directory);
            if (_files.Ready || !_server.ExeExists)
            {
                RenderPrepare();
                return;
            }

            string serverDir = _server.Directory;
            GameFiles.Status before = _files;
            List<string> steamDirs = Platform.SteamDirs();

            string done = await Task.Run(() =>
            {
                List<string> did = new List<string>();
                try
                {
                    if (!before.GameData || !before.Crafting)
                    {
                        string game = GameFiles.FindGame(steamDirs);
                        if (game != null && GameFiles.CanImportFrom(game))
                        {
                            GameFiles.ImportFromGame(game, serverDir);
                            did.Add(Lang.T("prep.importedGame", game));
                        }
                    }

                    if (!before.SteamClient)
                    {
                        foreach (string steam in steamDirs)
                        {
                            if (GameFiles.CopySteamClient(steam, serverDir))
                            {
                                did.Add(Lang.T("prep.copiedSteam", steam));
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                               || ex is InvalidDataException || ex is InvalidOperationException)
                {
                    did.Add(Lang.T("prep.autoFailed", ex.Message));
                }

                return string.Join("  ", did);
            });

            _files = GameFiles.Check(serverDir);
            if (done.Length > 0)
            {
                Say(done);
            }

            RenderPrepare();
        }

        private void RenderPrepare()
        {
            PrepareCard.Visibility = FilesReady || !_server.ExeExists ? Visibility.Collapsed : Visibility.Visible;

            PrepareList.ItemsSource = new List<CheckItem>
            {
                new CheckItem(_files.GameData ? Palette.Alive : Palette.Dead,
                    Lang.T(_files.GameData ? "prep.gameDataOk" : "prep.gameDataMissing")),
                new CheckItem(_files.Crafting ? Palette.Alive : Palette.Dead,
                    Lang.T(_files.Crafting ? "prep.craftingOk" : "prep.craftingMissing")),
                new CheckItem(_files.SteamClient ? Palette.Alive : Palette.Dead,
                    Lang.T(_files.SteamClient ? "prep.steamOk" : "prep.steamMissing")),
            };

            ImportGameButton.Visibility = _files.GameData && _files.Crafting ? Visibility.Collapsed : Visibility.Visible;
            SteamCmdButton.Visibility = _files.SteamClient ? Visibility.Collapsed : Visibility.Visible;
            StartButton.IsEnabled = _state == ServerState.Stopped && FilesReady;
        }

        /// <summary>服主选游戏文件夹 (从自己电脑拷到服务器上的那份也行): 选里面的 cachedata.sexp。</summary>
        private async void ImportGame_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("prep.pickTitle"),
                Filter = "cachedata.sexp|cachedata.sexp",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string gameDir = Path.GetDirectoryName(dialog.FileName);
            await RunPrepareStepAsync(() => Task.Run(() => GameFiles.ImportFromGame(gameDir, _server.Directory)),
                Lang.T("prep.importing"), Lang.T("prep.importedGame", gameDir));
        }

        private async void SteamCmd_Click(object sender, RoutedEventArgs e)
        {
            Progress<string> progress = new Progress<string>(step => PrepareNote.Text = step);
            await RunPrepareStepAsync(
                () => SteamCmd.FetchClientLibrariesAsync(_server.Directory, progress, CancellationToken.None),
                Lang.T("steamcmd.downloading"), Lang.T("prep.steamcmdDone"));
        }

        private async Task RunPrepareStepAsync(Func<Task> step, string working, string done)
        {
            if (_preparing)
            {
                return;
            }

            _preparing = true;
            ImportGameButton.IsEnabled = SteamCmdButton.IsEnabled = false;
            PrepareNote.Foreground = Palette.Soft;
            PrepareNote.Text = working;

            try
            {
                await step();
                PrepareNote.Text = done;
                Say(done);
            }
            catch (Exception ex)
            {
                PrepareNote.Foreground = Palette.Dead;
                PrepareNote.Text = Lang.T("prep.failed", ex.Message);
            }
            finally
            {
                _preparing = false;
                ImportGameButton.IsEnabled = SteamCmdButton.IsEnabled = true;
                _files = GameFiles.Check(_server.Directory);
                RenderPrepare();
            }
        }
    }
}
