// Main window: the optional "Rede de amigos" tab (virtual LAN, engine in Lan.cs).
// Off by default: the tab only exists while Opções > "Rede de amigos" is on, and the engine is only downloaded
// when a network is first turned on. Nothing here starts, stops or restarts the VPN tunnel.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace VpnPanel
{
    partial class MainWindow
    {
        enum LanState { Off, Installing, Starting, On, Stopping }

        RadioButton tabLan;
        FrameworkElement pageLan, lanEmpty, lanMain;
        CheckBox chkLanFeature, chkLanOn, chkLanProtect;
        TextBlock txtLanName, txtLanState, txtLanNote, txtLanIp, txtLanNick, txtLanPeersEmpty, txtLanInvite;
        Ellipse lanDot;
        StackPanel lanPeerList;
        WF.ToolStripMenuItem miLan;

        LanState lanState = LanState.Off;
        LanStatus lanStatus;
        string lanProgress, lanError, lanPeersKey;
        bool lanQueryRunning, lanSyncing;
        long lastLanQueryTick, lanUpSinceTick;
        int lanRestarts;

        void InitLan()
        {
            tabLan = E<RadioButton>("TabLan");
            pageLan = E<FrameworkElement>("PageLan");
            lanEmpty = E<FrameworkElement>("LanEmpty");
            lanMain = E<FrameworkElement>("LanMain");
            chkLanFeature = E<CheckBox>("ChkLanFeature");
            chkLanOn = E<CheckBox>("ChkLanOn");
            chkLanProtect = E<CheckBox>("ChkLanProtect");
            txtLanName = E<TextBlock>("TxtLanName");
            txtLanState = E<TextBlock>("TxtLanState");
            txtLanNote = E<TextBlock>("TxtLanNote");
            txtLanIp = E<TextBlock>("TxtLanIp");
            txtLanNick = E<TextBlock>("TxtLanNick");
            txtLanPeersEmpty = E<TextBlock>("TxtLanPeersEmpty");
            txtLanInvite = E<TextBlock>("TxtLanInvite");
            lanDot = E<Ellipse>("LanDot");
            lanPeerList = E<StackPanel>("LanPeerList");

            tabLan.Checked += (s, e) => ShowPage(pageLan);
            tabLan.Visibility = settings.LanEnabled ? Visibility.Visible : Visibility.Collapsed;

            lanSyncing = true;
            chkLanFeature.IsChecked = settings.LanEnabled;
            chkLanProtect.IsChecked = settings.LanProtect;
            lanSyncing = false;
            // Checked/Unchecked rather than Click, like every other switch
            RoutedEventHandler featureChanged = (s, e) => { if (!lanSyncing && !loading) SetLanFeature(chkLanFeature.IsChecked == true); };
            chkLanFeature.Checked += featureChanged;
            chkLanFeature.Unchecked += featureChanged;
            RoutedEventHandler onChanged = (s, e) => { if (!lanSyncing) SetLanOn(chkLanOn.IsChecked == true); };
            chkLanOn.Checked += onChanged;
            chkLanOn.Unchecked += onChanged;
            RoutedEventHandler protectChanged = (s, e) =>
            {
                if (lanSyncing) return;
                bool protect = chkLanProtect.IsChecked == true;
                settings.LanProtect = protect;
                settings.Save();
                if (lanState == LanState.Starting || lanState == LanState.On)
                    ThreadPool.QueueUserWorkItem(_ => Lan.SetProtect(protect));
            };
            chkLanProtect.Checked += protectChanged;
            chkLanProtect.Unchecked += protectChanged;

            E<Button>("BtnLanJoin").Click += (s, e) => JoinLan();
            E<Button>("BtnLanCreate").Click += (s, e) => CreateLan();
            E<Button>("BtnLanInvite").Click += (s, e) => CopyLanInvite();
            E<Button>("BtnLanLeave").Click += (s, e) => LeaveLan();
            E<Button>("BtnLanNick").Click += (s, e) => EditLanNick();
            E<Button>("BtnLanCopyIp").Click += (s, e) =>
            {
                if (lanStatus != null && lanStatus.MyIp != null && CopyText(lanStatus.MyIp)) FlashText(txtLanIp, "Copiado!");
            };
            E<Button>("BtnLanLog").Click += (s, e) =>
            {
                if (File.Exists(Lan.LogPath)) Process.Start("notepad.exe", "\"" + Lan.LogPath + "\"");
                else Dialogs.Info(w, "Ainda não há log", "O log é criado na primeira vez que a rede é ligada.");
            };
            UpdateLanUi();
        }

        string LanNick
        {
            get
            {
                string nick = settings.LanNick;
                if (string.IsNullOrEmpty(nick)) nick = Environment.UserName;
                if (string.IsNullOrEmpty(nick)) nick = Environment.MachineName;
                return Lan.CleanNick(nick);
            }
        }

        void LanUi(Action a)
        {
            var ui = Application.Current != null ? Application.Current.Dispatcher : null;
            if (ui != null) ui.BeginInvoke(new Action(() => { if (!exiting) a(); }));
        }

        // Called once the window is up: pick up an engine a previous instance left running, or bring the network back
        void StartLanOnLaunch()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool adopted = Lan.Adopt();
                if (!adopted && Lan.StaleState)
                {
                    // the last run ended without Stop() (reboot, crash): take its firewall rules out
                    Lan.Stop();
                }
                LanUi(() =>
                {
                    bool wanted = settings.LanEnabled && settings.LanOn && settings.LanNet != null;
                    if (adopted && wanted)
                    {
                        lanState = LanState.Starting;
                        lanUpSinceTick = NowTicks();
                        UpdateLanUi();
                    }
                    else if (adopted) StopLan();
                    else if (wanted) StartLan();
                });
            });
        }

        // ---------- the switches ----------

        // Opções > Rede de amigos: the whole feature
        void SetLanFeature(bool enabled)
        {
            settings.LanEnabled = enabled;
            if (!enabled) settings.LanOn = false;
            settings.Save();
            tabLan.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (enabled)
            {
                tabLan.IsChecked = true;
            }
            else
            {
                if (tabLan.IsChecked == true) tabHome.IsChecked = true;
                if (lanState != LanState.Off) StopLan();
            }
            UpdateLanUi();
        }

        // The network itself, from the Rede tab or the tray
        void SetLanOn(bool on)
        {
            if (settings.LanNet == null) return;
            settings.LanOn = on;
            settings.Save();
            lanRestarts = 0;
            lanError = null;
            if (on) StartLan();
            else StopLan();
            UpdateLanUi();
        }

        void StartLan()
        {
            if (settings.LanNet == null || lanState != LanState.Off) return;
            LanNetwork net = settings.LanNet;
            string nick = LanNick;
            bool protect = settings.LanProtect;
            bool install = !Lan.Installed;
            lanState = install ? LanState.Installing : LanState.Starting;
            lanProgress = install ? "Baixando o componente da rede..." : null;
            lanStatus = null;
            lanPeersKey = null;
            UpdateLanUi();
            new Thread(() =>
            {
                string error = null;
                if (install) error = Lan.Install(text => LanUi(() => { lanProgress = text; UpdateLanUi(); }));
                if (error == null) error = Lan.Start(net, nick, protect);
                LanUi(() =>
                {
                    if (error != null)
                    {
                        lanState = LanState.Off;
                        lanError = error;
                        settings.LanOn = false;
                        settings.Save();
                        NetMon.Record("Rede de amigos", NetMon.EventKind.Error, error);
                    }
                    else
                    {
                        lanState = LanState.Starting;
                        lanError = null;
                        lanUpSinceTick = NowTicks();
                        lastLanQueryTick = 0;
                        NetMon.Record("Rede de amigos", NetMon.EventKind.Info, "Rede de amigos ligada (" + net.Name + ")");
                        // switched off (or the feature disabled) while it was still downloading or starting
                        if (!settings.LanOn || !settings.LanEnabled || settings.LanNet == null) StopLan();
                    }
                    UpdateLanUi();
                });
            }) { IsBackground = true }.Start();
        }

        void StopLan()
        {
            // Installing: the worker sees LanOn == false when it finishes and stops by itself
            if (lanState == LanState.Off || lanState == LanState.Stopping || lanState == LanState.Installing) return;
            lanState = LanState.Stopping;
            lanStatus = null;
            lanPeersKey = null;
            UpdateLanUi();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Lan.Stop();
                LanUi(() =>
                {
                    lanState = LanState.Off;
                    NetMon.Record("Rede de amigos", NetMon.EventKind.Info, "Rede de amigos desligada");
                    // turned back on while it was stopping
                    if (settings.LanEnabled && settings.LanOn && settings.LanNet != null) StartLan();
                    UpdateLanUi();
                });
            });
        }

        // From Poll(): a liveness check on the process handle, and the member list only while someone is looking
        void PollLan()
        {
            if (lanState != LanState.Starting && lanState != LanState.On) return;
            if (!Lan.Running)
            {
                string detail = Lan.LastLogLine();
                NetMon.Record("Rede de amigos", NetMon.EventKind.Error, "A rede de amigos parou sozinha" + (detail != null ? ": " + detail : ""));
                if (settings.LanOn && lanRestarts < 3) lanRestarts++;
                else
                {
                    settings.LanOn = false;
                    settings.Save();
                    lanError = "A rede parou sozinha." + (detail != null ? " " + detail : "");
                }
                // drops the state file and the firewall rules, then starts again while LanOn is still set
                StopLan();
                return;
            }
            if (lanState == LanState.On && lanRestarts > 0 && ElapsedSeconds(lanUpSinceTick) > 90) lanRestarts = 0;

            bool looking = WindowShowing && pageLan.Visibility == Visibility.Visible;
            // Starting: ask until this PC has its address, also in the tray (the tray menu shows the state)
            if (lanState == LanState.On && !looking) return;
            double every = lanState == LanState.Starting ? 1.0 : 3.0;
            if (lanQueryRunning || (lastLanQueryTick != 0 && ElapsedSeconds(lastLanQueryTick) < every)) return;
            lanQueryRunning = true;
            lastLanQueryTick = NowTicks();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                LanStatus st = Lan.Query();
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui == null) { lanQueryRunning = false; return; }
                ui.BeginInvoke(new Action(() =>
                {
                    lanQueryRunning = false;
                    if (exiting || st == null || (lanState != LanState.Starting && lanState != LanState.On)) return;
                    lanStatus = st;
                    if (lanState == LanState.Starting && st.MyIp != null) lanState = LanState.On;
                    UpdateLanUi();
                }));
            });
        }

        // ---------- networks ----------

        void JoinLan()
        {
            string initial = "";
            try
            {
                string clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                string ignored;
                if (clip.Length < 2000 && Lan.ParseInvite(clip, out ignored) != null) initial = clip.Trim();
            }
            catch { }
            LanNetwork net = null;
            string[] values = LanForm("Entrar em uma rede", "Cole aqui o convite que o seu amigo mandou.", "Entrar",
                new[] { "Convite" }, new[] { initial }, new[] { Lan.InvitePrefix + "..." },
                v =>
                {
                    string error;
                    net = Lan.ParseInvite(v[0], out error);
                    return error;
                });
            if (values == null || net == null) return;
            EnterLan(net);
        }

        void CreateLan()
        {
            string node = settings.LanNodes.Count > 0 ? settings.LanNodes[0] : "";
            string cleanNode = null;
            string[] values = LanForm("Criar uma rede",
                "Dê um nome para a rede. O nó de encontro é o servidor que ajuda os PCs a se acharem pela internet: quem montou a sua rede sabe o endereço.",
                "Criar", new[] { "Nome da rede", "Nó de encontro" }, new[] { "", node }, new[] { "Ex.: Galera", "Ex.: udp://meu-no.exemplo.com:11010" },
                v =>
                {
                    if (Lan.CleanName(v[0]).Length < 2) return "Escreva um nome para a rede.";
                    cleanNode = Lan.NormalizeNode(v[1]);
                    if (cleanNode == null) return "O endereço do nó de encontro precisa ser no formato udp://endereço:porta.";
                    return null;
                });
            if (values == null || cleanNode == null) return;
            LanNetwork net = Lan.NewNetwork(values[0], new[] { cleanNode });
            EnterLan(net);
            bool copied = CopyText(Lan.MakeInvite(net));
            Dialogs.Info(w, "Rede criada", copied
                ? "O convite já está copiado: é só colar na conversa com os amigos. Eles abrem o VPN Panel, vão em Rede e escolhem “Entrar com um convite”."
                : "Use “Copiar convite” e mande para os amigos. Eles abrem o VPN Panel, vão em Rede e escolhem “Entrar com um convite”.");
        }

        void EnterLan(LanNetwork net)
        {
            settings.LanNet = net;
            settings.LanNodes = new List<string>(net.Nodes);
            lanError = null;
            SetLanOn(true);
        }

        void LeaveLan()
        {
            if (settings.LanNet == null) return;
            if (!Dialogs.Confirm(w, "Sair da rede?", "Você sai de “" + settings.LanNet.Name + "” neste PC. Para voltar é preciso o convite de novo.", "Sair da rede", true))
                return;
            settings.LanNodes = new List<string>(settings.LanNet.Nodes);
            settings.LanNet = null;
            settings.LanOn = false;
            settings.Save();
            lanError = null;
            StopLan();
            UpdateLanUi();
        }

        void CopyLanInvite()
        {
            if (settings.LanNet == null) return;
            if (CopyText(Lan.MakeInvite(settings.LanNet))) FlashText(txtLanInvite, "Convite copiado!");
        }

        void EditLanNick()
        {
            string nick = Dialogs.Prompt(w, "Seu nome na rede", "É como os amigos veem você na lista. Só letras e números, sem acento.", LanNick,
                v => v.Trim().Length == 0 ? "Escreva um nome." : null);
            if (nick == null) return;
            nick = Lan.CleanNick(nick);
            if (nick == LanNick) return;
            settings.LanNick = nick;
            settings.Save();
            // the name is given to the engine when it starts: the network restarts (the VPN tunnel is not touched)
            if (lanState == LanState.Starting || lanState == LanState.On) StopLan();
            UpdateLanUi();
        }

        // ---------- rendering ----------

        void UpdateLanUi()
        {
            if (pageLan == null) return;
            bool hasNet = settings.LanNet != null;
            lanEmpty.Visibility = hasNet ? Visibility.Collapsed : Visibility.Visible;
            lanMain.Visibility = hasNet ? Visibility.Visible : Visibility.Collapsed;
            if (!hasNet) return;

            txtLanName.Text = settings.LanNet.Name;
            txtLanNick.Text = LanNick;
            lanSyncing = true;
            chkLanOn.IsChecked = settings.LanOn;
            lanSyncing = false;

            int friends = lanStatus != null ? lanStatus.Peers.Count : 0;
            string text, color;
            switch (lanState)
            {
                case LanState.Installing:
                    text = lanProgress ?? "Preparando...";
                    color = "Warn";
                    break;
                case LanState.Starting:
                    // this PC only gets its address once a meeting node answers: without one it would wait forever
                    text = ElapsedSeconds(lanUpSinceTick) > 15
                        ? "Sem resposta do nó de encontro. Confira a internet; se continuar, o endereço do nó no convite pode estar errado ou fora do ar."
                        : "Entrando na rede...";
                    color = "Warn";
                    break;
                case LanState.Stopping:
                    text = "Desligando...";
                    color = "Muted";
                    break;
                case LanState.On:
                    if (friends > 0) { text = "Ligada · " + friends + (friends == 1 ? " amigo online" : " amigos online"); color = "Secure"; }
                    else if (lanStatus != null && lanStatus.NodeSeen) { text = "Ligada · só você por enquanto"; color = "Secure"; }
                    else { text = "Ligada, mas sem contato com o nó de encontro. Confira a internet."; color = "Warn"; }
                    break;
                default:
                    text = lanError ?? "Desligada";
                    color = lanError != null ? "Danger" : "Muted";
                    break;
            }
            txtLanState.Text = text;
            lanDot.Fill = B(color);
            txtLanIp.Text = lanStatus != null && lanStatus.MyIp != null ? lanStatus.MyIp : "—";

            // The LAN engine is an ordinary program for the VPN: in "Todos" its traffic rides inside the tunnel
            string note = null;
            if ((lanState == LanState.Starting || lanState == LanState.On) && wasRunning == true)
            {
                AppMode mode = EffectiveAppMode();
                if (mode == AppMode.All) note = "A VPN está no modo “Todos”: a rede de amigos passa por dentro dela e o ping pode subir. No modo “Só estes” isso não acontece.";
                else if (mode == AppMode.Except && !settings.Apps.Contains("easytier-core", StringComparer.OrdinalIgnoreCase) && settings.SharedApps)
                    note = "A VPN está no modo “Todos, exceto”: adicione easytier-core na aba Apps para a rede de amigos não passar por dentro dela.";
            }
            txtLanNote.Text = note ?? "";
            txtLanNote.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;

            RenderLanPeers();
        }

        void RenderLanPeers()
        {
            bool up = lanState == LanState.On;
            var peers = up && lanStatus != null ? lanStatus.Peers : new List<LanPeer>();
            var key = new StringBuilder(up ? "on" : "off");
            foreach (LanPeer p in peers)
                key.Append('|').Append(p.Name).Append(';').Append(p.Ip).Append(';').Append(p.Direct ? 'd' : 'r').Append(';').Append(Math.Round(p.LatencyMs));
            if (key.ToString() == lanPeersKey) return;
            lanPeersKey = key.ToString();

            lanPeerList.Children.Clear();
            foreach (LanPeer p in peers) lanPeerList.Children.Add(LanPeerRow(p));
            txtLanPeersEmpty.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            txtLanPeersEmpty.Text = up
                ? "Ninguém mais online. Mande o convite para os amigos: eles aparecem aqui quando ligarem a rede."
                : "Ligue a rede para ver quem está online.";
        }

        UIElement LanPeerRow(LanPeer peer)
        {
            var row = new Button
            {
                Style = Dialogs.S("GhostButton"),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
                ToolTip = "Copiar o IP de " + peer.Name,
            };
            AutomationProperties.SetName(row, peer.Name + ", " + peer.Ip);
            var dock = new DockPanel();
            var ping = new TextBlock
            {
                Text = peer.LatencyMs >= 0 ? Math.Round(peer.LatencyMs) + " ms" : "",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = peer.LatencyMs < 0 ? B("Muted") : peer.LatencyMs < 80 ? B("SecureText") : peer.LatencyMs < 160 ? B("Warn") : B("Danger"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            DockPanel.SetDock(ping, Dock.Right);
            dock.Children.Add(ping);
            var dot = new Ellipse { Width = 7, Height = 7, Fill = B("Secure"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            dock.Children.Add(dot);
            var lines = new StackPanel();
            lines.Children.Add(new TextBlock { Text = peer.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock
            {
                Text = peer.Ip + (peer.Direct ? "  ·  conexão direta" : "  ·  pelo nó de encontro"),
                Style = Dialogs.S("Caption"),
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 1, 0, 0),
            };
            lines.Children.Add(detail);
            dock.Children.Add(lines);
            row.Content = dock;
            row.Click += (s, e) => { if (CopyText(peer.Ip)) FlashText(detail, "IP copiado!"); };
            return row;
        }

        // Tray menu entry, refreshed when the menu opens
        void UpdateLanTray()
        {
            if (miLan == null) return;
            miLan.Visible = settings.LanEnabled && settings.LanNet != null;
            miLan.Text = settings.LanOn ? "Rede de amigos: desligar" : "Rede de amigos: ligar";
        }

        // ---------- small helpers ----------

        static bool CopyText(string text)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { return false; } // another program is holding the clipboard
        }

        // Shows a short confirmation in place of a label, then puts the label back
        void FlashText(TextBlock target, string message)
        {
            string original = target.Text;
            if (original == message) return;
            target.Text = message;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (target.Text == message) target.Text = original;
            };
            t.Start();
        }

        // A dialog with labelled text boxes. validate gets the trimmed values and returns an error or null.
        // Returns the values, or null when cancelled.
        string[] LanForm(string title, string message, string okText, string[] labels, string[] initial, string[] hints, Func<string[], string> validate)
        {
            Window d = Dialogs.Frame(w, title, 360);
            var body = new StackPanel();
            body.Children.Add(Dialogs.Message(message));
            var boxes = new TextBox[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                body.Children.Add(new TextBlock { Text = labels[i], FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(2, 12, 0, 5) });
                boxes[i] = new TextBox { Style = Dialogs.S("Input"), Text = initial[i] ?? "", Tag = hints[i] };
                AutomationProperties.SetName(boxes[i], labels[i]);
                body.Children.Add(boxes[i]);
            }
            var error = new TextBlock { Foreground = B("Danger"), FontSize = 12, Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            body.Children.Add(error);
            Button ok = Dialogs.Btn(okText, "PrimaryButton");
            Button cancel = Dialogs.Btn("Cancelar", "GhostButton");
            ok.IsDefault = true;
            cancel.IsCancel = true;
            string[] values = null;
            ok.Click += (s, e) =>
            {
                var v = boxes.Select(b => b.Text.Trim()).ToArray();
                string msg = validate(v);
                if (msg == null) { values = v; d.DialogResult = true; return; }
                error.Text = msg;
                error.Visibility = Visibility.Visible;
            };
            Dialogs.Layout(d, title, body, cancel, ok);
            d.Loaded += (s, e) => { boxes[0].Focus(); boxes[0].SelectAll(); };
            return d.ShowDialog() == true ? values : null;
        }
    }
}
