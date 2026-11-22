// Main window: Servers tab: list, ping, selection, fastest server and imports.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace VpnPanel
{
    partial class MainWindow
    {
        void RefreshConfigs()
        {
            string[] names = Paths.ConfigNames();
            if (!string.IsNullOrEmpty(settings.PreferredConfigOverride) &&
                !names.Contains(settings.PreferredConfigOverride, StringComparer.OrdinalIgnoreCase))
            {
                settings.PreferredConfigOverride = null;
            }
            if (settings.Config == null || !names.Contains(settings.Config, StringComparer.OrdinalIgnoreCase))
            {
                settings.Config = names.FirstOrDefault();
                settings.Save();
            }
            active = settings.Config != null ? ConfigInfo.Read(names.First(n => n.Equals(settings.Config, StringComparison.OrdinalIgnoreCase))) : null;
            if (active != null && active.Endpoint != null && active.Place == null) Geo.Lookup(active.Endpoint, ScheduleGeoRefresh);

            // the cards are only built while someone can see them; hidden, they wait for OnVisibilityChanged or ShowPage
            if (ServersShowing) RenderServerList();
            else serversDirty = true;

            string code = active != null ? active.CountryCode : null;
            string flag = CountryFlag(code);
            txtFlag.Text = flag ?? (code ?? "");
            txtFlagGlobe.Visibility = (code != null || flag != null) ? Visibility.Collapsed : Visibility.Visible;
            txtSrvName.Text = active != null ? active.Name : "—";
            txtSrvDetail.Text = active == null ? "" : ServerDetail(active);
            txtSrvDetail.ToolTip = active == null ? null : active.Detail;
            UpdateHomePing();
            SyncDownloadsWatcher();
            UpdateLockAvailability();
            UpdateDiagRouteBadge();
            if (txtStatus != null && wasRunning != null) UpdateState();
        }

        void ScheduleGeoRefresh()
        {
            geoDebounce.Stop();
            geoDebounce.Start();
        }

        readonly List<KeyValuePair<ConfigInfo, UIElement>> serverCardItems = new List<KeyValuePair<ConfigInfo, UIElement>>();
        TextBlock noMatchLabel;

        void RenderServerList()
        {
            serversDirty = false;
            string[] names = Paths.ConfigNames();
            serverList.Children.Clear();
            serverCardItems.Clear();
            pingLabels.Clear();

            if (serverSearchBox != null)
                serverSearchBox.Visibility = names.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (string n in names)
            {
                ConfigInfo c = active != null && n == active.Name ? active : ConfigInfo.Read(n);
                if (c.Endpoint != null && c.Place == null) Geo.Lookup(c.Endpoint, ScheduleGeoRefresh);
                UIElement card = ServerCard(c);
                serverList.Children.Add(card);
                serverCardItems.Add(new KeyValuePair<ConfigInfo, UIElement>(c, card));
            }

            if (names.Length > 0)
            {
                if (noMatchLabel == null)
                {
                    noMatchLabel = new TextBlock
                    {
                        Text = "Nenhum servidor corresponde à pesquisa.",
                        Foreground = B("Muted"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 24, 0, 0),
                        Visibility = Visibility.Collapsed,
                    };
                }
                serverList.Children.Add(noMatchLabel);
            }

            FilterServerList();
            emptyServers.Visibility = names.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // a server just added gets measured too (the others are still fresh in the cache)
            MeasureLatency(false);
        }

        void FilterServerList()
        {
            if (serversDirty) { RenderServerList(); return; }
            string query = txtServerSearch != null ? txtServerSearch.Text.Trim() : null;
            int matches = 0;
            foreach (var kv in serverCardItems)
            {
                ConfigInfo c = kv.Key;
                bool match = true;
                if (!string.IsNullOrEmpty(query))
                {
                    match = c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match && c.Place != null)
                    {
                        if (c.Place.Country != null && c.Place.Country.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && c.Place.CountryName != null && c.Place.CountryName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && c.Place.City != null && c.Place.City.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    }
                    if (!match && c.GuessedCountry != null && c.GuessedCountry.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    if (!match && c.Server != null && c.Server.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    if (!match && c.Endpoint != null && c.Endpoint.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                }
                kv.Value.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) matches++;
            }
            if (noMatchLabel != null)
                noMatchLabel.Visibility = serverCardItems.Count > 0 && matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // "Houston, Estados Unidos" once the location is known; server label and IP until then
        static string ServerDetail(ConfigInfo c)
        {
            Geo.Place p = c.Readable ? c.Place : null;
            if (p == null) return c.Detail;
            return string.IsNullOrEmpty(p.City) ? p.CountryName : p.City + ", " + p.CountryName;
        }

        UIElement ServerCard(ConfigInfo c)
        {
            bool isActive = active != null && c.Name == active.Name;
            bool live = IsLive(c.Name);

            var card = new Button
            {
                Style = Dialogs.S("CardButton"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 12, 10, 12),
            };
            AutomationProperties.SetName(card, "Usar " + c.Name);
            if (isActive) { card.BorderBrush = B("Accent"); card.Background = B("ActiveCard"); }

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string code = c.CountryCode;
            string flag = CountryFlag(code);
            var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = isActive ? B("AccentSoft") : B("Surface2"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
            badge.Child = flag != null
                ? (UIElement)new TextBlock { Text = flag, FontSize = 17, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : (code != null
                    ? (UIElement)new TextBlock { Text = code, FontWeight = FontWeights.Bold, Foreground = isActive ? B("Accent") : B("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    : (UIElement)new TextBlock { Text = "\uE774", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 16, Foreground = isActive ? B("Accent") : B("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(badge);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = c.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 100 });
            if (live || isActive)
            {
                var pill = new Border { Background = live ? B("AccentSoft") : B("Hover"), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pill.Child = new TextBlock { Text = live ? "Conectado" : "Selecionado", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = live ? B("Accent") : B("Muted") };
                titleRow.Children.Add(pill);
            }
            text.Children.Add(titleRow);
            text.Children.Add(new TextBlock { Text = ServerDetail(c), ToolTip = c.Detail, Style = Dialogs.S("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            if (!settings.SharedApps && c.Readable && c.Mode != AppMode.All)
                text.Children.Add(new TextBlock { Text = (c.Mode == AppMode.Only ? "Só " : "Exceto ") + JoinPt(c.Apps), Style = Dialogs.S("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (c.Endpoint != null)
            {
                var ping = new PingLabel(c.Endpoint);
                List<PingLabel> same;
                if (!pingLabels.TryGetValue(c.Endpoint, out same)) pingLabels[c.Endpoint] = same = new List<PingLabel>();
                same.Add(ping);
                UpdatePingLabel(ping);
                ping.Root.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(ping.Root);
            }
            var rename = new Button { Style = Dialogs.S("IconButton"), Content = "", ToolTip = "Renomear" };
            var delete = new Button { Style = Dialogs.S("IconDangerButton"), Content = "", ToolTip = "Excluir" };
            AutomationProperties.SetName(rename, "Renomear " + c.Name);
            AutomationProperties.SetName(delete, "Excluir " + c.Name);
            rename.Click += (s, e) => { e.Handled = true; RenameConfig(c.Name); };
            delete.Click += (s, e) => { e.Handled = true; DeleteConfig(c.Name); };
            actions.Children.Add(rename);
            actions.Children.Add(delete);
            Grid.SetColumn(actions, 2);
            g.Children.Add(actions);

            card.Content = g;
            card.Click += (s, e) => SelectConfig(c.Name);
            return card;
        }

        // ---------- latency ----------

        // The ping shown on one server card: a colored dot and "42 ms"
        class PingLabel
        {
            public readonly string Host;
            public readonly StackPanel Root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MinWidth = 50, Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent };
            public readonly Ellipse Dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) };
            public readonly TextBlock Text = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

            public PingLabel(string host)
            {
                Host = host;
                Root.Children.Add(Dot);
                Root.Children.Add(Text);
                ToolTipService.SetInitialShowDelay(Root, 250);
            }
        }

        readonly Dictionary<string, List<PingLabel>> pingLabels = new Dictionary<string, List<PingLabel>>(StringComparer.OrdinalIgnoreCase);

        bool ServersShowing { get { return WindowShowing && pageServers.Visibility == Visibility.Visible; } }

        // Measures all servers while on Servidores, or just the active server while on Início
        void MeasureLatency(bool force)
        {
            if (ServersShowing)
            {
                var hosts = Paths.ConfigNames().Select(n => ConfigInfo.Read(n).Endpoint).Where(h => h != null).ToList();
                Latency.Measure(hosts, force, wasRunning == true);
                UpdateAllPingLabels(); // "···" on what was just queued
            }
            else if (WindowShowing && pageHome.Visibility == Visibility.Visible && active != null && !string.IsNullOrEmpty(active.Endpoint))
            {
                lastHomePing = DateTime.UtcNow;
                Latency.Measure(new[] { active.Endpoint }, force, wasRunning == true);
                UpdateHomePing();
            }
        }

        void OnLatencyMeasured(string host)
        {
            List<PingLabel> labels;
            if (pingLabels.TryGetValue(host, out labels))
                foreach (PingLabel l in labels) UpdatePingLabel(l);
            if (active != null && host.Equals(active.Endpoint, StringComparison.OrdinalIgnoreCase)) UpdateHomePing();
            TryConnectFastestPending();
        }

        void UpdateAllPingLabels()
        {
            foreach (List<PingLabel> labels in pingLabels.Values)
                foreach (PingLabel l in labels) UpdatePingLabel(l);
            UpdateHomePing();
        }

        static Brush PingBrush(int ms) { return B(ms < 80 ? "Accent" : ms <= 180 ? "Warn" : "Danger"); }

        // Through a full tunnel, pings to the other servers take the detour via the connected one
        string PingTip(string host, Latency.Result r)
        {
            if (!r.Ok)
                return "Sem resposta ao ping (ICMP).\nMuitos servidores WireGuard bloqueiam ping, então isso não quer dizer que o servidor esteja fora do ar.";
            string tip = "Latência média: " + r.Ms + " ms";
            if (r.JitterMs > 0 || r.LossPct > 0)
                tip += string.Format(" · Jitter: {0} ms · Perda: {1}%", r.JitterMs, r.LossPct);
            bool liveHost = active != null && IsLive(active.Name) && host.Equals(active.Endpoint, StringComparison.OrdinalIgnoreCase);
            if (r.ViaTunnel && !liveHost) tip += "\nMedido com a VPN ligada: inclui o caminho pelo servidor conectado.";
            return tip;
        }

        void UpdatePingLabel(PingLabel l)
        {
            Latency.Result r = Latency.Cached(l.Host);
            if (r == null)
            {
                bool measuring = Latency.IsPending(l.Host);
                l.Dot.Visibility = Visibility.Collapsed;
                l.Text.Text = measuring ? "···" : "";
                l.Text.Foreground = B("Muted");
                l.Root.ToolTip = measuring ? "Medindo a latência..." : null;
                AutomationProperties.SetName(l.Root, measuring ? "Medindo latência" : "");
                return;
            }
            l.Dot.Visibility = r.Ok ? Visibility.Visible : Visibility.Collapsed;
            if (r.Ok) l.Dot.Fill = PingBrush(r.Ms);
            l.Text.Text = r.Ok ? r.Ms + " ms" : "sem ping";
            l.Text.Foreground = r.Ok ? PingBrush(r.Ms) : B("Muted");
            l.Root.ToolTip = PingTip(l.Host, r);
            AutomationProperties.SetName(l.Root, r.Ok ? "Latência " + r.Ms + " milissegundos" : "Servidor não responde a ping");
        }

        // Início only shows what Servidores already measured; it never pings by itself
        void UpdateHomePing()
        {
            Latency.Result r = active != null ? Latency.Cached(active.Endpoint) : null;
            if (r == null) { txtSrvPing.Visibility = Visibility.Collapsed; return; }
            txtSrvPing.Visibility = Visibility.Visible;
            txtSrvPing.Text = r.Ok ? "● " + r.Ms + " ms" : "sem ping";
            txtSrvPing.Foreground = r.Ok ? PingBrush(r.Ms) : B("Muted");
            txtSrvPing.ToolTip = PingTip(active.Endpoint, r);
        }

        bool IsLive(string name)
        {
            return wasRunning == true && runningConfig != null && runningConfig.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        void SelectConfig(string name)
        {
            if (active != null && active.Name == name) { tabHome.IsChecked = true; return; }
            StopContinuousDiag("Interrompido: servidor alterado");
            settings.PreferredConfigOverride = null;
            failedServersThisDrop.Clear();
            settings.Config = name;
            settings.Save();
            RefreshConfigs();
            LoadAppsEditor();
            // already connected: switch servers right away
            if (wasRunning == true) Reconnect();
            tabHome.IsChecked = true;
        }

        // From the tray: always ends up connected to that server (connects, or switches and reconnects)
        void SwitchServer(string name)
        {
            if (!File.Exists(Paths.Conf(name))) return;
            bool same = active != null && active.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
            if (!same && wasRunning == true && !userStopped &&
                !ConfirmLiveInterruption("Trocar de servidor durante a chamada?",
                    "Trocar de servidor reinicia o túnel e derruba a chamada ou a transmissão por alguns segundos.", "Trocar"))
                return;
            if (!same)
            {
                StopContinuousDiag("Interrompido: servidor alterado");
                settings.PreferredConfigOverride = null;
                failedServersThisDrop.Clear();
                settings.Config = name;
                settings.Save();
                RefreshConfigs();
                LoadAppsEditor();
            }
            ResetAutoReconnectState();
            if (wasRunning == true && userStopped)
            {
                // the old client is still exiting: Poll connects once it's gone
                reconnectPending = true;
                UpdateState();
            }
            else if (wasRunning == true) { if (!same) Reconnect(); }
            else if (connecting)
            {
                // started but not seen running yet: replace it instead of running two clients
                if (same) return;
                reconnectPending = true;
                Disconnect(false);
            }
            else Connect();
        }

        static List<ConfigInfo> ServersWithEndpoints(string[] names)
        {
            var list = new List<ConfigInfo>(names.Length);
            foreach (string n in names)
            {
                ConfigInfo c = ConfigInfo.Read(n);
                if (!string.IsNullOrEmpty(c.Endpoint)) list.Add(c);
            }
            return list;
        }

        static string FastestRatedServer(List<ConfigInfo> list)
        {
            string bestName = null;
            int bestScore = int.MaxValue;
            int bestMs = int.MaxValue;
            foreach (ConfigInfo c in list)
            {
                Latency.Result r = Latency.Cached(c.Endpoint);
                if (r != null && r.Ok && (r.Score < bestScore || (r.Score == bestScore && r.Ms < bestMs)))
                {
                    bestScore = r.Score;
                    bestMs = r.Ms;
                    bestName = c.Name;
                }
            }
            return bestName;
        }

        void ConnectFastest()
        {
            string[] names = Paths.ConfigNames();
            if (names.Length == 0)
            {
                if (WindowShowing) Dialogs.Info(w, "Nenhum servidor", "Adicione um servidor antes de conectar.");
                return;
            }
            if (names.Length == 1)
            {
                SwitchServer(names[0]);
                if (WindowShowing) tabHome.IsChecked = true;
                return;
            }

            List<ConfigInfo> list = ServersWithEndpoints(names);
            if (list.Count == 0)
            {
                SwitchServer(names[0]);
                if (WindowShowing) tabHome.IsChecked = true;
                return;
            }

            string best = FastestRatedServer(list);
            if (best != null)
            {
                SwitchServer(best);
                if (WindowShowing) tabHome.IsChecked = true;
                return;
            }

            bool allTested = list.All(s => Latency.Cached(s.Endpoint) != null);
            if (allTested)
            {
                SwitchServer(list[0].Name);
                if (WindowShowing)
                {
                    tabHome.IsChecked = true;
                    Dialogs.Info(w, "Latência indisponível", "Os servidores não respondem a ping ICMP. Conectando ao primeiro servidor.");
                }
                return;
            }

            // Pings not yet cached: measure asynchronously and connect automatically
            connectingFastestPending = true;
            fastestStarted = DateTime.UtcNow;
            if (fastestTimer.IsEnabled) fastestTimer.Stop();
            fastestTimer.Start();

            var hosts = list.Select(s => s.Endpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Latency.Measure(hosts, true, wasRunning == true);
            if (WindowShowing)
            {
                tabServers.IsChecked = true;
                UpdateAllPingLabels();
            }
            else
            {
                tray.ShowBalloonTip(2500, "VPN Panel", "Medindo latência dos servidores... Conectando ao mais rápido em instantes.", WF.ToolTipIcon.Info);
            }
        }

        void FinishConnectFastest()
        {
            if (!connectingFastestPending) return;
            connectingFastestPending = false;
            if (fastestTimer.IsEnabled) fastestTimer.Stop();

            List<ConfigInfo> list = ServersWithEndpoints(Paths.ConfigNames());
            if (list.Count == 0) return;

            string target = FastestRatedServer(list) ?? list[0].Name;
            SwitchServer(target);
            if (WindowShowing) tabHome.IsChecked = true;
        }

        void TryConnectFastestPending()
        {
            if (!connectingFastestPending) return;
            List<ConfigInfo> list = ServersWithEndpoints(Paths.ConfigNames());
            if (list.Count == 0)
            {
                connectingFastestPending = false;
                if (fastestTimer.IsEnabled) fastestTimer.Stop();
                return;
            }

            bool allDone = list.All(s => Latency.Cached(s.Endpoint) != null);
            string best = FastestRatedServer(list);

            if (allDone || (best != null && (DateTime.UtcNow - fastestStarted).TotalSeconds >= 1.5))
            {
                FinishConnectFastest();
            }
        }

        void RenameConfig(string name)
        {
            if (IsLive(name)) { Dialogs.Info(w, "Servidor em uso", "Desconecte a VPN antes de renomear este servidor."); return; }
            string newName = Dialogs.Prompt(w, "Renomear servidor", null, name, n => ConfFile.CheckName(n, name));
            if (newName == null || newName == name) return;
            try
            {
                ConfFile.Rename(name, newName);
                settings.RenameRecent(name, newName);
                if (settings.Config == name) settings.Config = newName;
                settings.Save();
            }
            catch (Exception ex) { Dialogs.Info(w, "Não foi possível renomear", ex.Message); }
            RefreshConfigs();
            LoadAppsEditor();
        }

        void DeleteConfig(string name)
        {
            if (IsLive(name)) { Dialogs.Info(w, "Servidor em uso", "Desconecte a VPN antes de excluir este servidor."); return; }
            if (!Dialogs.Confirm(w, "Excluir " + name + "?", "O arquivo vai para a Lixeira do Windows, então dá para recuperar depois.", "Excluir", true)) return;
            try { ConfFile.Delete(name); }
            catch (Exception ex) { Dialogs.Info(w, "Não foi possível excluir", ex.Message); }
            settings.Recent.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            settings.Save();
            RefreshConfigs();
            MigrateSharedApps();
            LoadAppsEditor();
            UpdateSetup();
            UpdateState();
        }

        static IEnumerable<string> DroppedConfs(DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            return (files ?? new string[0]).Where(ConfFile.IsImportable);
        }

        void BrowseImport()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Adicionar servidor",
                Filter = "Arquivo de servidor WireGuard (*.conf, *.zip)|*.conf;*.zip",
                Multiselect = true,
            };
            if (ofd.ShowDialog(w) == true) Import(ofd.FileNames);
        }

        void ExportBackup()
        {
            try
            {
                if (!Directory.Exists(Paths.Configs) || !Directory.GetFiles(Paths.Configs, "*.conf").Any())
                {
                    Dialogs.Info(w, "Nenhum servidor", "Você ainda não tem nenhum servidor configurado para exportar.");
                    return;
                }
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Exportar backup dos servidores",
                    Filter = "Arquivo Zip (*.zip)|*.zip",
                    FileName = "VPN-Panel-Servidores-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip"
                };
                if (sfd.ShowDialog(w) == true)
                {
                    string tmpZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpn_backup_" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        System.IO.Compression.ZipFile.CreateFromDirectory(Paths.Configs, tmpZip);
                        if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                        File.Move(tmpZip, sfd.FileName);
                        Dialogs.Info(w, "Backup concluído", "Seus servidores foram salvos em:\n" + sfd.FileName);
                    }
                    finally
                    {
                        if (File.Exists(tmpZip)) { try { File.Delete(tmpZip); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                Dialogs.Info(w, "Erro no backup", "Não foi possível salvar o backup: " + ex.Message);
            }
        }

        void Import(IEnumerable<string> files)
        {
            var errors = new List<string>();
            var added = new List<string>();
            foreach (string f in files)
            {
                try { added.AddRange(ConfFile.Import(f)); }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(f) + ": " + ex.Message); }
            }
            Imported(added, errors);
        }

        void Imported(List<string> added, List<string> errors)
        {
            bool wasReady = Ready;
            if (added.Count > 0 && active == null) settings.Config = added[0];
            RefreshConfigs();
            MigrateSharedApps();
            LoadAppsEditor();
            UpdateSetup();
            UpdateState();
            // finishing setup lands on Início, ready to connect; otherwise show the new server in the list
            if (added.Count > 0) { if (!wasReady && Ready) tabHome.IsChecked = true; else tabServers.IsChecked = true; }
            if (errors.Count > 0) Dialogs.Info(w, added.Count > 0 ? "Alguns arquivos não foram adicionados" : "Arquivo não adicionado", string.Join("\n", errors));
        }

        // Ctrl+V on the window; false when the clipboard has nothing to import
        bool Paste()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(ConfFile.IsImportable).ToList();
                    if (files.Count == 0) return false;
                    Import(files);
                    return true;
                }
                string text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (!ConfFile.IsWireGuard(text)) return false;
                string name = Dialogs.Prompt(w, "Servidor colado", "Dê um nome para este servidor:", PastedName(text), n => ConfFile.CheckName(n, null));
                if (name != null) Imported(new List<string> { ConfFile.ImportText(text, name) }, new List<string>());
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Info(w, "Não deu para colar", ex.Message);
                return true;
            }
        }

        // The endpoint's host name (node-br-05 from node-br-05.example.net), or "Servidor"
        static string PastedName(string text)
        {
            Match m = Regex.Match(text, @"^\s*Endpoint\s*=\s*([A-Za-z][\w-]*)\.", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            string baseName = m.Success ? m.Groups[1].Value : "Servidor";
            string name = baseName;
            for (int n = 2; File.Exists(Paths.Conf(name)); n++) name = baseName + " " + n;
            return name;
        }

        // ---------- servers arriving in Downloads ----------

        // Expect a download during setup, and for a while after the guide was opened
        bool ExpectingDownload { get { return active == null || DateTime.Now < expectDownloadUntil; } }

        void SyncDownloadsWatcher()
        {
            if (downloadsWatcher == null) return;
            try { downloadsWatcher.EnableRaisingEvents = ExpectingDownload; } catch { }
        }

        void OpenGuide(bool otherProvider)
        {
            if (guide != null) { guide.Activate(); return; }
            expectDownloadUntil = DateTime.Now.AddMinutes(20);
            SyncDownloadsWatcher();
            guide = Dialogs.ProviderGuide(w, otherProvider);
            bool browse = guide.ShowDialog() == true;
            guide = null;
            if (browse) BrowseImport();
        }

        void WatchDownloads()
        {
            string dir = Paths.Downloads;
            if (dir == null || !Directory.Exists(dir)) return;
            downloadsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            downloadsTimer.Tick += (s, e) => CheckDownloads();
            try
            {
                downloadsWatcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler seen = (s, e) =>
                {
                    if (e != null && ConfFile.IsImportable(e.FullPath))
                        w.Dispatcher.BeginInvoke(new Action(() => OnDownload(e.FullPath)));
                };
                downloadsWatcher.Created += seen;
                downloadsWatcher.Changed += seen;
                downloadsWatcher.Renamed += (s, e) =>
                {
                    if (e != null && ConfFile.IsImportable(e.FullPath))
                        w.Dispatcher.BeginInvoke(new Action(() => OnDownload(e.FullPath)));
                };
                downloadsWatcher.EnableRaisingEvents = ExpectingDownload;
            }
            catch { downloadsWatcher = null; }
        }

        static string DownloadKey(string path)
        {
            try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks; } catch { return path; }
        }

        void OnDownload(string path)
        {
            if (downloadsTimer == null || !ExpectingDownload || !ConfFile.IsImportable(path) || handledDownloads.Contains(DownloadKey(path))) return;
            if (!pendingDownloads.ContainsKey(path)) pendingDownloads[path] = DateTime.Now;
            downloadsTimer.Start();
        }

        // Browsers write the file in steps (empty file, .part, rename): import once it reads as a server
        void CheckDownloads()
        {
            foreach (string path in pendingDownloads.Keys.ToList())
            {
                bool ready = false;
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } // fails while still being written
                    ready = ConfFile.ReadConfs(path).Count > 0;
                }
                catch { }
                if (ready)
                {
                    pendingDownloads.Remove(path);
                    handledDownloads.Add(DownloadKey(path));
                    Downloaded(path);
                }
                else if ((DateTime.Now - pendingDownloads[path]).TotalSeconds > 30) pendingDownloads.Remove(path);
            }
            if (pendingDownloads.Count == 0) downloadsTimer.Stop();
        }

        void Downloaded(string path)
        {
            if (guide != null) guide.Close();
            int before = Paths.ConfigNames().Length;
            ShowWindow();
            Import(new[] { path });
            int added = Paths.ConfigNames().Length - before;
            if (added > 0)
                tray.ShowBalloonTip(3000, "VPN Panel", added == 1 ? "Servidor adicionado. Pronto para conectar." : added + " servidores adicionados.", WF.ToolTipIcon.Info);
        }

        // Newest server file in Downloads from the last week, if any
        static string RecentDownload()
        {
            try
            {
                string dir = Paths.Downloads;
                if (dir == null) return null;
                return new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => ConfFile.IsImportable(f.Name) && f.LastWriteTime > DateTime.Now.AddDays(-7))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(10)
                    .Select(f => f.FullName)
                    .FirstOrDefault(f => { try { return ConfFile.ReadConfs(f).Count > 0; } catch { return false; } });
            }
            catch { return null; }
        }

        // ---------- apps (split tunneling) ----------
    }
}
