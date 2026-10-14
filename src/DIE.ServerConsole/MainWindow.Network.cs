using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>网络页: Windows 防火墙、外网能不能连进来、路由器 UPnP、云服务器安全组。</summary>
    public partial class MainWindow
    {
        private FirewallPlan.Result _firewall;
        private string _firewallError;
        private bool _autoFixTried;
        private ProbeResult _probe;
        private string _probeError;
        private bool _probing;

        private PortsBlock CurrentPorts() => _overview?.Ports ?? new PortsBlock
        {
            Request = 1555, Match = 2555, Status = 8555, UdpFirst = 50000, UdpLast = 50099,
        };

        private IReadOnlyList<FirewallPlan.Need> Needs()
        {
            PortsBlock ports = CurrentPorts();
            return FirewallPlan.Needs(ports.Request, ports.Match, ports.Status, ports.UdpFirst, ports.UdpLast);
        }

        // ==================================================================
        // Windows 防火墙
        // ==================================================================

        /// <param name="autoFix">缺规则就直接补 (控制台打开时这样做, 每次打开只补一回)。</param>
        private async Task CheckFirewallAsync(bool autoFix)
        {
            IReadOnlyList<FirewallPlan.Need> needs = Needs();
            string exe = _server.ExePath;

            try
            {
                _firewall = await Task.Run(() =>
                {
                    (List<FirewallPlan.Rule> rules, bool on) = Firewall.Read();
                    return FirewallPlan.Evaluate(rules, needs, on, exe);
                });
                _firewallError = null;
            }
            catch (Exception ex)
            {
                _firewall = null;
                _firewallError = ex.Message;
            }

            RenderFirewall();
            RenderChecks();

            if (autoFix && !_autoFixTried && _firewall != null && !_firewall.AllGood)
            {
                _autoFixTried = true;
                Say(Lang.T("msg.fwFixing"));
                await FixFirewallAsync();
            }
            else if (_firewall != null && _firewall.AllGood && autoFix)
            {
                Say(Lang.T("msg.fwOk"));
            }
        }

        private async Task FixFirewallAsync()
        {
            List<string> commands = FirewallPlan.FixCommands(Needs(), _firewall, _server.ExePath);
            bool ran = await Task.Run(() => Firewall.RunElevated(commands));

            if (!ran)
            {
                Say(Lang.T("msg.fwDeclined"));
                return;
            }

            await CheckFirewallAsync(autoFix: false);
            Say(_firewall != null && _firewall.AllGood ? Lang.T("msg.fwFixed") : Lang.T("msg.fwStillBad"));
        }

        private async void FirewallFix_Click(object sender, RoutedEventArgs e)
        {
            FirewallFixButton.IsEnabled = false;
            try
            {
                if (_firewall == null)
                {
                    await CheckFirewallAsync(autoFix: false);
                }

                await FixFirewallAsync();
            }
            finally
            {
                FirewallFixButton.IsEnabled = true;
            }
        }

        private async void FirewallCheck_Click(object sender, RoutedEventArgs e) =>
            await CheckFirewallAsync(autoFix: false);

        private void RenderFirewall()
        {
            List<CheckItem> rows = new List<CheckItem>();

            if (_firewall == null)
            {
                rows.Add(new CheckItem(Palette.Faint, _firewallError == null
                    ? Lang.T("fw.checking")
                    : Lang.T("fw.readFailed", _firewallError)));
            }
            else
            {
                foreach ((FirewallPlan.Need need, bool allowed) in _firewall.Ports)
                {
                    rows.Add(new CheckItem(allowed ? Palette.Alive : Palette.Dead,
                        Lang.T(allowed ? "fw.portOk" : "fw.portBad", need.Label, PortPurpose(need))));
                }

                foreach (string rule in _firewall.BlockingRules)
                {
                    rows.Add(new CheckItem(Palette.Dead, Lang.T("fw.blocking", rule)));
                }
            }

            FirewallList.ItemsSource = rows;
            FirewallFixButton.Content = Lang.T(_firewall != null && _firewall.AllGood ? "fw.refix" : "fw.fix");
        }

        private string PortPurpose(FirewallPlan.Need need)
        {
            PortsBlock ports = CurrentPorts();
            if (need.Protocol == FirewallPlan.ProtocolUdp)
            {
                return Lang.T("port.battle");
            }

            return need.First == ports.Request ? Lang.T("port.lobby")
                : need.First == ports.Match ? Lang.T("port.queue")
                : Lang.T("port.status");
        }

        // ==================================================================
        // 外网探测 (登记处反过来连本机)
        // ==================================================================

        private async void Probe_Click(object sender, RoutedEventArgs e) => await ProbeAsync(quiet: false);

        private async Task ProbeAsync(bool quiet)
        {
            if (_probing)
            {
                return;
            }

            if (_state != ServerState.Running)
            {
                if (!quiet)
                {
                    Say(Lang.T("msg.probeNeedsServer"));
                }

                return;
            }

            _probing = true;
            ProbeButton.IsEnabled = false;
            _probeError = null;
            RenderProbe(testing: true);

            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    _probe = await _admin.ProbeAsync(_settings.RegistryHost, ConsoleSettings.RegistryPort,
                        CurrentPorts().Status, timeout.Token);
                }

                if (_probe == null)
                {
                    _probeError = Lang.T("probe.unsupported");
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException
                                           || ex is OperationCanceledException)
            {
                _probe = null;
                _probeError = Lang.T("probe.registryDown", _settings.RegistryHost);
            }
            finally
            {
                _probing = false;
                ProbeButton.IsEnabled = true;
            }

            RenderProbe();
            RenderChecks();

            if (!quiet || (_probe != null && !_probe.Ports.All(p => p.Open)))
            {
                Say(_probe == null ? _probeError
                    : _probe.Ports.All(p => p.Open) ? Lang.T("msg.probeOk")
                    : Lang.T("msg.probeBad"));
            }
        }

        private void RenderProbe(bool testing = false)
        {
            List<CheckItem> rows = new List<CheckItem>();

            if (testing)
            {
                rows.Add(new CheckItem(Palette.Hazard, Lang.T("probe.testing", _settings.RegistryHost)));
            }
            else if (_probe == null)
            {
                rows.Add(new CheckItem(Palette.Faint, _probeError ?? Lang.T("probe.notYet")));
            }
            else
            {
                PortsBlock ports = CurrentPorts();
                foreach (ProbePort port in _probe.Ports)
                {
                    string purpose = port.Port == ports.Request ? Lang.T("port.lobby")
                        : port.Port == ports.Match ? Lang.T("port.queue")
                        : Lang.T("port.status");
                    rows.Add(new CheckItem(port.Open ? Palette.Alive : Palette.Dead,
                        Lang.T(port.Open ? "probe.open" : "probe.closed", port.Port, purpose)));
                }

                rows.Add(new CheckItem(Palette.Warn, Lang.T("probe.udp", ports.UdpFirst, ports.UdpLast)));
                rows.Add(new CheckItem(Palette.Soft, Lang.T("probe.ip", _probe.Ip)));

                if (_probe.Ip == _settings.RegistryHost && !_probe.Ports.All(p => p.Open))
                {
                    rows.Add(new CheckItem(Palette.Warn, Lang.T("probe.selfRegistry")));
                }
            }

            ProbeList.ItemsSource = rows;

            string host = ServerFiles.ReadHost(_server.Directory);
            bool offer = _probe != null && !string.IsNullOrEmpty(_probe.Ip) && host != _probe.Ip;
            UseProbeIpButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            UseProbeIpButton.Content = offer ? Lang.T("probe.useIp", _probe.Ip) : string.Empty;
        }

        private void UseProbeIp_Click(object sender, RoutedEventArgs e)
        {
            if (_probe == null || string.IsNullOrEmpty(_probe.Ip))
            {
                return;
            }

            HostBox.Text = _probe.Ip;
            NavSettings.IsChecked = true;
            SaveNote.Text = Lang.T("set.hostFilled");
        }

        // ==================================================================
        // 路由器 UPnP
        // ==================================================================

        private async void Upnp_Click(object sender, RoutedEventArgs e)
        {
            UpnpButton.IsEnabled = false;
            UpnpResult.Visibility = Visibility.Visible;
            UpnpResult.Text = Lang.T("upnp.working");

            PortsBlock ports = CurrentPorts();
            List<(string, int)> wanted = new List<(string, int)>
            {
                ("TCP", ports.Request),
                ("TCP", ports.Match),
                ("TCP", ports.Status),
            };
            for (int port = ports.UdpFirst; port <= ports.UdpLast; port++)
            {
                wanted.Add(("UDP", port));
            }

            try
            {
                Upnp.Outcome outcome = await Task.Run(() => Upnp.Map(wanted));
                UpnpResult.Text = !outcome.Supported
                    ? Lang.T("upnp.unsupported")
                    : outcome.Failed == 0
                        ? Lang.T("upnp.ok", outcome.Mapped, outcome.LocalAddress)
                        : Lang.T("upnp.partial", outcome.Mapped, outcome.Failed);
            }
            catch (Exception ex)
            {
                UpnpResult.Text = Lang.T("upnp.failed", ex.Message);
            }
            finally
            {
                UpnpButton.IsEnabled = true;
            }
        }

        // ==================================================================
        // 云服务器安全组 (只能服主自己去控制台加, 这里给出要加的那几条)
        // ==================================================================

        private void RenderCloudRules()
        {
            PortsBlock ports = CurrentPorts();
            CloudRules.Text = string.Join("\r\n", new[]
            {
                Lang.T("cloud.rule", "TCP", ports.Request + "/" + ports.Request, Lang.T("port.lobby")),
                Lang.T("cloud.rule", "TCP", ports.Match + "/" + ports.Match, Lang.T("port.queue")),
                Lang.T("cloud.rule", "TCP", ports.Status + "/" + ports.Status, Lang.T("port.status")),
                Lang.T("cloud.rule", "UDP", ports.UdpFirst + "/" + ports.UdpLast, Lang.T("port.battle")),
            });
        }
    }
}
