using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>概览 / 对局 / 玩家三页: 把管理接口拿到的全貌画出来, 外加负载采样。</summary>
    public partial class MainWindow
    {
        /// <summary>战斗服泵线程忙到这个比例就算吃紧 (再高玩家会感觉卡)。</summary>
        private const double BusyWarn = 50;
        private const double BusyBad = 80;

        private List<PlayerItem> _players = new List<PlayerItem>();

        private void RenderOverview()
        {
            Overview o = _overview;
            PortsBlock ports = o?.Ports ?? new PortsBlock
            {
                Request = 1555, Match = 2555, Status = 8555, UdpFirst = 50000, UdpLast = 50099,
            };

            LobbyTitle.Text = Lang.T("layer.lobby", ports.Request);
            QueueTitle.Text = Lang.T("layer.queue", ports.Match);
            BattleTitle.Text = Lang.T("layer.battle", ports.UdpFirst, ports.UdpLast);

            if (o == null)
            {
                LobbyNumber.Text = QueueNumber.Text = BattleNumber.Text = BattleLoad.Text = "–";
                LobbyDetail.Text = QueueDetail.Text = BattleDetail.Text = BattleLoadDetail.Text = string.Empty;
                MatchesGrid.ItemsSource = null;
                MatchesSummary.Text = string.Empty;
                _players = new List<PlayerItem>();
                ShowPlayers();
                RenderChecks();
                RenderState();
                return;
            }

            // ---- 三层 ----
            LobbyNumber.Text = o.Lobby.Online.ToString(CultureInfo.InvariantCulture);
            LobbyDetail.Text = Lang.T("layer.lobbyDetail", o.Lobby.Idle);

            QueueNumber.Text = o.Matchmaking.Total.ToString(CultureInfo.InvariantCulture);
            List<string> queueLines = o.Matchmaking.Queues
                .Select(q => Lang.T("layer.queueLine", Format.Mode(q.Queue), q.Waiting, q.Readying))
                .ToList();
            if (o.Matchmaking.Rooms > 0 || o.Matchmaking.RoomPlayers > 0)
            {
                queueLines.Add(Lang.T("layer.rooms", o.Matchmaking.Rooms, o.Matchmaking.RoomPlayers));
            }

            if (queueLines.Count == 0)
            {
                queueLines.Add(Lang.T("layer.queueEmpty"));
            }

            queueLines.Add(Lang.T("layer.links", o.Matchmaking.Links));
            QueueDetail.Text = string.Join("\n", queueLines);

            BattleNumber.Text = o.Battle.Players.ToString(CultureInfo.InvariantCulture);
            BattleDetail.Text = Lang.T("layer.battleDetail", o.Battle.Matches, o.Battle.MaxMatches);

            // ---- 战斗服负载: 最忙的那一局 ----
            MatchRow busiest = o.Matches.Where(m => m.State != "over")
                .OrderByDescending(m => m.BusyPercent).FirstOrDefault();
            if (busiest == null)
            {
                BattleLoad.Text = "–";
                BattleLoad.Foreground = Palette.Strong;
                BattleLoadDetail.Text = Lang.T("load.noMatches");
            }
            else
            {
                BattleLoad.Text = Format.Percent(busiest.BusyPercent);
                BattleLoad.Foreground = LoadBrush(busiest.BusyPercent);
                BattleLoadDetail.Text = Lang.T("load.battleDetail", busiest.Port,
                    busiest.AverageFrameMs.ToString("0.0", CultureInfo.InvariantCulture),
                    busiest.WorstFrameMs.ToString("0", CultureInfo.InvariantCulture));
            }

            // ---- 对局表 ----
            MatchesGrid.ItemsSource = o.Matches.Select(m => new MatchItem
            {
                Port = m.Port.ToString(CultureInfo.InvariantCulture),
                Mode = Format.Mode(m.Queue),
                Map = string.Join("  ·  ", new[] { m.Map, Format.Difficulty(m.Queue, m.Difficulty) }
                    .Where(part => !string.IsNullOrEmpty(part))),
                Players = m.Players + " / " + m.Roster,
                State = Format.State(m.State),
                StateBrush = m.State == "running" ? Palette.Alive
                    : m.State == "error" ? Palette.Dead
                    : m.State == "over" ? Palette.Faint
                    : Palette.Hazard,
                Age = Format.Duration(m.AgeSeconds),
                Load = m.Frames() ? Format.Percent(m.BusyPercent) : "–",
                LoadBrush = LoadBrush(m.BusyPercent),
                Frame = m.Frames()
                    ? m.AverageFrameMs.ToString("0.0", CultureInfo.InvariantCulture) + " / "
                      + m.WorstFrameMs.ToString("0", CultureInfo.InvariantCulture) + " ms"
                    : "–",
            }).ToList();
            MatchesSummary.Text = Lang.T("matches.summary", o.Battle.Matches, o.Battle.MaxMatches, o.Battle.Players);

            // ---- 玩家表 ----
            _players = o.Players.Select(p => new PlayerItem
            {
                Name = string.IsNullOrEmpty(p.Name) ? "#" + p.UserId : p.Name,
                Where = Format.Where(p.Where, p.Port),
                WhereBrush = p.Where == "match" ? Palette.Alive : p.Where == "queue" ? Palette.Hazard : Palette.Soft,
                Online = p.OnlineSeconds > 0 ? Format.Duration(p.OnlineSeconds) : "–",
                SteamId = p.SteamId ?? string.Empty,
                Address = p.Address ?? string.Empty,
            }).ToList();
            ShowPlayers();

            RenderChecks();
            RenderState();
        }

        private static Brush LoadBrush(double busy) =>
            busy >= BusyBad ? Palette.Dead : busy >= BusyWarn ? Palette.Warn : Palette.Alive;

        private void ShowPlayers()
        {
            string search = (PlayerSearch.Text ?? string.Empty).Trim();
            object selected = PlayersGrid.SelectedItem;

            PlayersGrid.ItemsSource = search.Length == 0
                ? _players
                : _players.Where(p => Contains(p.Name, search) || Contains(p.SteamId, search)
                                      || Contains(p.Address, search)).ToList();

            if (selected is PlayerItem was)
            {
                PlayersGrid.SelectedItem = _players.FirstOrDefault(p => p.SteamId == was.SteamId && p.Name == was.Name);
            }

            PlayersTitle.Text = Lang.T("players.title", _players.Count);
        }

        private static bool Contains(string text, string part) =>
            (text ?? string.Empty).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private void PlayerSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowPlayers();

        private void CopySteamId_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (PlayersGrid.SelectedItem is PlayerItem player && player.SteamId.Length > 0)
            {
                System.Windows.Clipboard.SetText(player.SteamId);
                Say(Lang.T("msg.copied", player.SteamId));
            }
        }

        private void OpenProfile_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (PlayersGrid.SelectedItem is PlayerItem player && ulong.TryParse(player.SteamId, out ulong id))
            {
                Platform.OpenUrl("https://steamcommunity.com/profiles/" + id.ToString(CultureInfo.InvariantCulture));
            }
        }

        // ==================================================================
        // 负载
        // ==================================================================

        private void SampleLoad()
        {
            double machineCpu = _load.MachineCpu();
            (long usedMb, long totalMb) = _load.MachineMemory();
            MachineCpu.Text = Format.Percent(machineCpu);
            MachineDetail.Text = Lang.T("load.machineDetail",
                (usedMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture),
                (totalMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture), Environment.ProcessorCount);

            Process process = _server.IsRunning ? _server.Current : null;
            double processCpu = double.NaN;
            if (process != null)
            {
                processCpu = _load.ProcessCpu(process);
                try
                {
                    ProcessDetail.Text = Lang.T("load.processDetail", process.WorkingSet64 / (1024 * 1024),
                        process.Threads.Count);
                }
                catch (InvalidOperationException)
                {
                    ProcessDetail.Text = string.Empty;
                }
            }
            else
            {
                ProcessDetail.Text = Lang.T("load.notRunning");
            }

            ProcessCpu.Text = Format.Percent(processCpu);

            PlayersChart.Add(_overview?.Lobby.Online ?? 0);
            CpuChart.Add(double.IsNaN(processCpu) ? 0 : processCpu);
            PlayersChartPeak.Text = Lang.T("chart.peak", PlayersChart.Peak.ToString("0", CultureInfo.InvariantCulture));
            CpuChartPeak.Text = Lang.T("chart.peak", Format.Percent(CpuChart.Peak));
        }

        // ==================================================================
        // 开服检查 (概览页最下面)
        // ==================================================================

        private void RenderChecks()
        {
            List<CheckItem> checks = new List<CheckItem>();

            if (_firewall == null)
            {
                checks.Add(new CheckItem(Palette.Faint, Lang.T("check.fwUnknown")));
            }
            else if (_firewall.AllGood)
            {
                checks.Add(new CheckItem(Palette.Alive, Lang.T("check.fwOk")));
            }
            else
            {
                checks.Add(new CheckItem(Palette.Dead, Lang.T("check.fwBad")));
            }

            if (_probe == null)
            {
                checks.Add(_probeError == null
                    ? new CheckItem(Palette.Faint, Lang.T("check.probeUnknown"))
                    : new CheckItem(Palette.Warn, Lang.T("check.probeError", _probeError)));
            }
            else if (_probe.Ports.All(p => p.Open))
            {
                checks.Add(new CheckItem(Palette.Alive, Lang.T("check.probeOk", _probe.Ip)));
            }
            else
            {
                checks.Add(new CheckItem(Palette.Dead, Lang.T("check.probeBad")));
            }

            string host = _overview?.PublicHost ?? ServerFiles.ReadHost(_server.Directory);
            checks.Add(string.IsNullOrEmpty(host)
                ? new CheckItem(Palette.Warn, Lang.T("check.noHost"))
                : _probe != null && !string.IsNullOrEmpty(_probe.Ip) && host != _probe.Ip && IsIp(host)
                    ? new CheckItem(Palette.Warn, Lang.T("check.hostMismatch", host, _probe.Ip))
                    : new CheckItem(Palette.Alive, Lang.T("check.hostOk", host)));

            bool listed = _overview?.Listed ?? ServerFiles.ReadInfo(_server.Directory).Listed;
            checks.Add(listed
                ? new CheckItem(Palette.Alive, Lang.T("check.listed"))
                : new CheckItem(Palette.Faint, Lang.T("check.unlisted")));

            CheckList.ItemsSource = checks;
        }

        private static bool IsIp(string host) => System.Net.IPAddress.TryParse(host, out _);
    }

    internal static class MatchRowExtensions
    {
        /// <summary>这一局汇总过帧数没有 (战斗服每 30 秒汇总一次, 刚开的局还没有)。</summary>
        public static bool Frames(this MatchRow match) => match.AverageFrameMs > 0 || match.BusyPercent > 0;
    }
}
