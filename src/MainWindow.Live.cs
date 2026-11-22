// Main window: Modo Live. Discord call detection, live quality of the tunnel, the Discord card on Início,
// the guards that keep a call or stream from being cut, and the "best server for live" test.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VpnPanel
{
    partial class MainWindow
    {
        const double DiscordScanSec = 3.0;
        const double LiveSampleSec = 2.0, IdleSampleSec = 4.0;
        const int QualityProbeTimeoutMs = 900;
        const int FarServerMs = 120; // steady ping above this gets the "Distante" badge

        // Início: Discord card
        Border discordCard, discordBadge;
        Ellipse discordDot;
        TextBlock txtDiscordState, txtDiscordDetail, txtDiscordBadge, lblIpLoc, lblIpOrg;
        Image imgDiscord;
        bool discordIconLoaded;
        string discordCardKey = "";
        CheckBox chkLiveGuard;
        Button btnLiveTest;
        bool liveTestRunning;

        // Is Discord open / in a call (see DiscordWatch); debounced so one odd scan changes nothing
        bool discordScanRunning, discordRunning, discordLive;
        int discordLiveStreak, discordIdleStreak;
        long lastDiscordScanTick, liveSinceTick;

        // Quality of the path through the tunnel, sampled with small DNS probes while a call is on
        // (or while someone is looking at Início). Independent of the liveness probe on purpose.
        readonly LinkQuality linkQuality = new LinkQuality();
        NetDiag.TunnelUdpSession qualitySession;
        bool qualityRunning, qualityUnavailable;
        int qualityGen;
        long lastQualityTick, lastQualityOkTick, firstLostTick, incidentStartTick, lastLiveBalloonTick, lastSpikeEventTick;
        LinkQuality.Cause incidentCause;
        int liveIncidents, liveLongestIncidentSec, liveHeldReconnects;
        bool liveHealSkipNoted, liveNotedThisCall;

        void InitLive()
        {
            discordCard = E<Border>("DiscordCard");
            discordBadge = E<Border>("DiscordBadge");
            discordDot = E<Ellipse>("DiscordDot");
            txtDiscordState = E<TextBlock>("TxtDiscordState");
            txtDiscordDetail = E<TextBlock>("TxtDiscordDetail");
            txtDiscordBadge = E<TextBlock>("TxtDiscordBadge");
            imgDiscord = E<Image>("ImgDiscord");
            lblIpLoc = E<TextBlock>("LblIpLoc");
            lblIpOrg = E<TextBlock>("LblIpOrg");
            chkLiveGuard = E<CheckBox>("ChkLiveGuard");
            btnLiveTest = E<Button>("BtnLiveTest");

            discordCard.MouseLeftButtonUp += (s, e) => { if (tabDiag != null) tabDiag.IsChecked = true; };
            btnLiveTest.Click += (s, e) => TestServersForLive();
            chkLiveGuard.IsChecked = settings.LiveGuard;
            RoutedEventHandler onGuard = (s, e) =>
            {
                if (loading) return;
                settings.LiveGuard = chkLiveGuard.IsChecked == true;
                settings.Save();
                discordCardKey = "";
                UpdateDiscordCard();
            };
            chkLiveGuard.Checked += onGuard;
            chkLiveGuard.Unchecked += onGuard;
        }

        // The split tunneling that is in effect (connected) or would be (disconnected)
        void CurrentRouting(out AppMode mode, out IList<string> apps)
        {
            mode = AppMode.All;
            apps = null;
            if (shown == Conn.On && active != null)
            {
                mode = active.Mode;
                apps = active.Apps;
            }
            else if (settings.SharedApps)
            {
                mode = settings.AppsMode;
                apps = settings.Apps;
            }
            else if (active != null)
            {
                mode = active.Mode;
                apps = active.Apps;
            }
        }

        bool RoutingTunnelsDiscord()
        {
            AppMode mode;
            IList<string> apps;
            CurrentRouting(out mode, out apps);
            return DiscordWatch.TunnelsDiscord(mode, apps);
        }

        // The card says everything the chip row would when Discord is the only app in "Só estes"
        static bool DiscordCardReplacesChips(AppMode mode, IList<string> apps)
        {
            return mode == AppMode.Only && apps != null && apps.Count == 1 && DiscordWatch.IsDiscordApp(apps[0]);
        }

        // A Discord call or stream is running through this tunnel and Modo Live is on
        bool LiveNow
        {
            get
            {
                return settings.LiveGuard && discordLive && wasRunning == true && !userStopped && RoutingTunnelsDiscord();
            }
        }

        // Called from Poll (every 1 s with the window open, every 3 s connected in the tray)
        void PollLive()
        {
            if (discordCard == null) return;
            bool relevant = Ready && RoutingTunnelsDiscord();
            bool tunnelOn = wasRunning == true;
            bool watching = WindowShowing && pageHome.Visibility == Visibility.Visible;
            bool transition = connecting || reconnectPending || (reconnectTimer != null && reconnectTimer.IsEnabled);

            if (relevant && (tunnelOn || watching || transition))
            {
                if (!discordScanRunning && (lastDiscordScanTick == 0 || ElapsedSeconds(lastDiscordScanTick) >= DiscordScanSec))
                    ScanDiscordAsync();
            }
            else if (discordRunning || discordLive)
            {
                // nobody needs the answer (VPN off and window hidden, or Discord is not routed): stop watching
                ApplyDiscordSnapshot(new DiscordWatch.Snapshot(), true);
            }

            bool live = LiveNow;
            bool sample = relevant && tunnelOn && monitoring && handshakeOk && shown == Conn.On && !reconnectPending && (live || watching);
            if (sample && !qualityRunning &&
                (lastQualityTick == 0 || ElapsedSeconds(lastQualityTick) >= (live ? LiveSampleSec : IdleSampleSec)))
            {
                RunQualitySample();
            }
            if (watching) UpdateDiscordCard();
        }

        void ScanDiscordAsync()
        {
            discordScanRunning = true;
            lastDiscordScanTick = NowTicks();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                DiscordWatch.Snapshot snap = null;
                try { snap = DiscordWatch.Scan(); } catch { }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui == null) { discordScanRunning = false; return; }
                ui.BeginInvoke(new Action(() =>
                {
                    discordScanRunning = false;
                    if (exiting || snap == null) return;
                    ApplyDiscordSnapshot(snap, false);
                }));
            });
        }

        void ApplyDiscordSnapshot(DiscordWatch.Snapshot snap, bool immediate)
        {
            discordRunning = snap.Running;
            if (snap.InCall)
            {
                discordIdleStreak = 0;
                if (!discordLive && (immediate || ++discordLiveStreak >= 2)) SetDiscordLive(true);
            }
            else
            {
                discordLiveStreak = 0;
                if (discordLive && (immediate || ++discordIdleStreak >= 3)) SetDiscordLive(false);
            }
        }

        void SetDiscordLive(bool live)
        {
            if (discordLive == live) return;
            discordLive = live;
            discordLiveStreak = 0;
            discordIdleStreak = 0;
            discordCardKey = "";
            if (live)
            {
                liveSinceTick = NowTicks();
                liveIncidents = 0;
                liveLongestIncidentSec = 0;
                liveHeldReconnects = 0;
                liveHealSkipNoted = false;
                linkQuality.ResetSession();
                liveNotedThisCall = LiveNow;
                if (liveNotedThisCall)
                    RecordLive(NetMon.EventKind.Info, "Chamada ou transmissão do Discord detectada — Modo Live ativo");
            }
            else
            {
                if (liveNotedThisCall || linkQuality.SessionSamples > 0)
                    RecordLive(NetMon.EventKind.Info, LiveSummary());
                liveNotedThisCall = false;
                incidentStartTick = 0;
                firstLostTick = 0;
            }
            if (WindowShowing) UpdateDiscordCard();
        }

        static string FormatDuration(double seconds)
        {
            int min = (int)Math.Round(seconds / 60.0);
            if (min < 1) return "menos de 1 min";
            if (min < 60) return min + " min";
            return (min / 60) + "h" + (min % 60).ToString("00");
        }

        string LiveSummary()
        {
            var sb = new StringBuilder("Chamada do Discord encerrada após " + FormatDuration(ElapsedSeconds(liveSinceTick)));
            if (linkQuality.SessionSamples > 0)
            {
                sb.Append(": ping médio ").Append(linkQuality.SessionAvgMs).Append(" ms · perda ")
                  .Append(LinkQuality.FormatPct(linkQuality.SessionLossPct));
                if (liveIncidents > 0)
                    sb.Append(" · ").Append(liveIncidents).Append(liveIncidents == 1 ? " falha do túnel" : " falhas do túnel")
                      .Append(" (maior ~").Append(liveLongestIncidentSec).Append(" s)");
                else sb.Append(" · nenhuma falha do túnel");
                if (liveHeldReconnects > 0)
                    sb.Append(" · ").Append(liveHeldReconnects).Append(liveHeldReconnects == 1 ? " reinício evitado" : " reinícios evitados");
            }
            return sb.ToString();
        }

        // Timeline of the Testes tab plus a small file, so what happened in a call survives closing the app
        void RecordLive(NetMon.EventKind kind, string message)
        {
            NetMon.Record("Discord", kind, message);
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + kind + "] " + message + Environment.NewLine;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (liveHistoryLock)
                    {
                        string path = System.IO.Path.Combine(Paths.Data, "live-history.log");
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 256 * 1024)
                        {
                            string all = File.ReadAllText(path);
                            int cut = all.IndexOf('\n', all.Length / 2);
                            File.WriteAllText(path, cut >= 0 ? all.Substring(cut + 1) : "");
                        }
                        File.AppendAllText(path, line);
                    }
                }
                catch { }
            });
        }

        static readonly object liveHistoryLock = new object();

        // ---------------------------------------------------------------- quality sampling

        void ResetLinkQuality(bool dropSession)
        {
            if (incidentStartTick != 0)
            {
                int secs = Math.Max(1, (int)Math.Round(ElapsedSeconds(incidentStartTick)));
                if (discordLive)
                {
                    liveIncidents++;
                    if (secs > liveLongestIncidentSec) liveLongestIncidentSec = secs;
                }
                RecordLive(NetMon.EventKind.Warn, "Túnel sem resposta por ~" + secs + " s até ser encerrado — " + CauseText(incidentCause));
            }
            qualityGen++;
            qualityRunning = false;
            qualityUnavailable = false;
            linkQuality.ResetWindow();
            lastQualityTick = 0;
            lastQualityOkTick = 0;
            firstLostTick = 0;
            incidentStartTick = 0;
            discordCardKey = "";
            if (dropSession && qualitySession != null)
            {
                NetDiag.TunnelUdpSession old = qualitySession;
                qualitySession = null;
                ThreadPool.QueueUserWorkItem(_ => { try { old.Dispose(); } catch { } });
            }
        }

        static string CauseText(LinkQuality.Cause cause)
        {
            switch (cause)
            {
                case LinkQuality.Cause.Vpn: return "a internet direta respondeu: a falha foi na rota ou no servidor da VPN";
                case LinkQuality.Cause.Internet: return "a sua internet também falhou (não foi a VPN)";
                default: return "causa não identificada";
            }
        }

        // One small DNS query through the tunnel. In "Só estes" it goes through VpnPanelProbe (inside the
        // tunnel) and a lost one is compared with the panel's own direct path, which tells whose fault it was.
        void RunQualitySample()
        {
            // sampling was paused (window hidden, no call): the old numbers say nothing about now
            if (lastQualityTick != 0 && ElapsedSeconds(lastQualityTick) > 30) linkQuality.ResetWindow();
            string cfgName = runningConfig ?? (active != null ? active.Name : null);
            AppMode mode = cfgName != null ? ConfigInfo.Read(cfgName).Mode : AppMode.All;
            bool viaHelper = mode == AppMode.Only;
            if (viaHelper && !(Tunnel.ActiveEngine == TunnelEngine.WireSock && Tunnel.ProbeAppInjected))
            {
                // a tunnel started outside the app has no probe inside it
                lastQualityTick = NowTicks();
                if (!qualityUnavailable) { qualityUnavailable = true; discordCardKey = ""; }
                return;
            }
            qualityUnavailable = false;
            qualityRunning = true;
            lastQualityTick = NowTicks();
            int gen = qualityGen;
            NetDiag.TunnelUdpSession session = viaHelper ? qualitySession : null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                NetDiag.TunnelUdpSession created = null;
                bool valid = true, ok = false;
                int ms = -1;
                LinkQuality.Cause cause = LinkQuality.Cause.None;
                try
                {
                    NetDiag.ProbeResult r;
                    if (viaHelper)
                    {
                        if (session == null || !session.IsReady)
                        {
                            created = new NetDiag.TunnelUdpSession();
                            session = created;
                        }
                        if (!session.IsReady) valid = false;
                        else
                        {
                            r = session.Query("1.1.1.1", 53, QualityProbeTimeoutMs, CancellationToken.None);
                            if ((r == null || !r.Ok) && session.IsReady)
                                r = session.Query("8.8.8.8", 53, QualityProbeTimeoutMs, CancellationToken.None);
                            ok = r != null && r.Ok;
                            if (ok) ms = r.LatencyMs;
                            else if (!session.IsReady) valid = false; // the helper died: not a verdict on the tunnel
                            else
                            {
                                NetDiag.ProbeResult direct = NetDiag.ProbeUdpDns("1.1.1.1", 53, QualityProbeTimeoutMs, CancellationToken.None);
                                cause = direct != null && direct.Ok ? LinkQuality.Cause.Vpn : LinkQuality.Cause.Internet;
                            }
                        }
                    }
                    else
                    {
                        // Todos / Todos exceto: the panel's own traffic is inside the tunnel
                        r = NetDiag.ProbeUdpDns("1.1.1.1", 53, QualityProbeTimeoutMs, CancellationToken.None);
                        if (r == null || !r.Ok)
                            r = NetDiag.ProbeUdpDns("8.8.8.8", 53, QualityProbeTimeoutMs, CancellationToken.None);
                        ok = r != null && r.Ok;
                        if (ok) ms = r.LatencyMs;
                        else cause = NetDiag.HasPhysicalUplink() ? LinkQuality.Cause.Unknown : LinkQuality.Cause.Internet;
                    }
                }
                catch { valid = false; }

                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui == null)
                {
                    if (created != null) { try { created.Dispose(); } catch { } }
                    return;
                }
                ui.BeginInvoke(new Action(() =>
                {
                    bool current = gen == qualityGen && !exiting && monitoring && wasRunning == true;
                    if (created != null)
                    {
                        NetDiag.TunnelUdpSession stale = current ? qualitySession : created;
                        if (current) qualitySession = created;
                        if (stale != null) ThreadPool.QueueUserWorkItem(__ => { try { stale.Dispose(); } catch { } });
                    }
                    if (gen != qualityGen) return;
                    qualityRunning = false;
                    if (!current || !valid) return;
                    OnQualitySample(ok, ms, cause);
                }));
            });
        }

        void OnQualitySample(bool ok, int ms, LinkQuality.Cause cause)
        {
            linkQuality.Add(ok, ms);
            bool live = LiveNow;
            if (ok)
            {
                lastQualityOkTick = NowTicks();
                firstLostTick = 0;
                if (incidentStartTick != 0)
                {
                    int secs = Math.Max(1, (int)Math.Round(ElapsedSeconds(incidentStartTick)));
                    incidentStartTick = 0;
                    if (discordLive)
                    {
                        liveIncidents++;
                        if (secs > liveLongestIncidentSec) liveLongestIncidentSec = secs;
                    }
                    RecordLive(NetMon.EventKind.Warn,
                        (live ? "Durante a chamada, o túnel ficou" : "Túnel") + " sem resposta por ~" + secs + " s — " + CauseText(incidentCause));
                }
                else if (linkQuality.Count >= 8 && ms >= Math.Max(250, linkQuality.AvgMs * 3) &&
                         (lastSpikeEventTick == 0 || ElapsedSeconds(lastSpikeEventTick) >= 60))
                {
                    lastSpikeEventTick = NowTicks();
                    RecordLive(NetMon.EventKind.Warn, "Pico de latência no túnel: " + ms + " ms (média " + linkQuality.AvgMs + " ms)");
                }
            }
            else
            {
                if (linkQuality.ConsecutiveLost == 1) firstLostTick = lastQualityTick;
                if (linkQuality.ConsecutiveLost >= 2 && incidentStartTick == 0)
                {
                    incidentStartTick = firstLostTick != 0 ? firstLostTick : NowTicks();
                    incidentCause = cause;
                }
                else if (incidentStartTick != 0 && cause == LinkQuality.Cause.Internet)
                {
                    incidentCause = cause;
                }
                if (live && incidentStartTick != 0 && !WindowShowing && ElapsedSeconds(incidentStartTick) >= 6 &&
                    (lastLiveBalloonTick == 0 || ElapsedSeconds(lastLiveBalloonTick) >= 300))
                {
                    lastLiveBalloonTick = NowTicks();
                    Balloon(incidentCause == LinkQuality.Cause.Internet
                        ? "Sua internet falhou durante a chamada do Discord. A VPN não é a causa."
                        : "A VPN está sem resposta durante a chamada do Discord. Se não voltar, troque de servidor.");
                }
            }
            if (WindowShowing) UpdateDiscordCard();
        }

        // ---------------------------------------------------------------- guards

        // The liveness probe (or the handshake watchdog) says the tunnel is dead, but the independent quality
        // sampler got an answer through it moments ago. Restarting would cut a working call, so keep it.
        bool TryHoldTunnelForLive()
        {
            if (!LiveNow || lastQualityOkTick == 0 || ElapsedSeconds(lastQualityOkTick) > 6) return false;
            liveHeldReconnects++;
            RecordLive(NetMon.EventKind.Warn,
                "Uma sonda acusou túnel parado, mas a medição da chamada respondeu há instantes — o túnel não foi reiniciado");
            rehandshakeSuspectTick = 0;
            rehandshakeGen++;
            rehandshakeConfirmedDown = false;
            rehandshakeOnlyChecked10s = false;
            ResetLiveness(true); // fresh probe helper next time: the old one may be what is stuck
            return true;
        }

        // Anything the user starts that restarts or stops the tunnel asks first while a call is on
        bool ConfirmLiveInterruption(string title, string consequence, string okText)
        {
            if (!LiveNow) return true;
            return Dialogs.Confirm(w, title, "O Discord está em chamada ou transmitindo pela VPN. " + consequence, okText, true);
        }

        void ReconnectByUser()
        {
            if (wasRunning == true && !ConfirmLiveInterruption("Reconectar durante a chamada?",
                    "Reconectar derruba a chamada ou a transmissão por alguns segundos.", "Reconectar"))
                return;
            Reconnect();
        }

        // ---------------------------------------------------------------- Discord card (Início)

        // Início has no spare height: while the Discord card shows, the IP card keeps only its first row
        // and the "IP of the apps outside the VPN" note moves to a tooltip.
        void SyncIpNote()
        {
            bool partial = shown == Conn.On && active != null && active.Mode == AppMode.Only;
            bool cardShown = discordCard != null && discordCard.Visibility == Visibility.Visible;
            Visibility note = partial && !cardShown ? Visibility.Visible : Visibility.Collapsed;
            if (txtIpNote.Visibility != note) txtIpNote.Visibility = note;
            if (lblIpLoc == null) return;
            Visibility rows = cardShown ? Visibility.Collapsed : Visibility.Visible;
            if (lblIpLoc.Visibility != rows)
            {
                lblIpLoc.Visibility = rows;
                lblIpOrg.Visibility = rows;
                txtLoc.Visibility = rows;
                txtOrg.Visibility = rows;
            }
            txtIp.ToolTip = cardShown && partial ? txtIpNote.Text : null;
        }

        void UpdateDiscordCard()
        {
            if (discordCard == null) return;
            bool on = shown == Conn.On;
            bool relevant = Ready && RoutingTunnelsDiscord();
            bool busy = shown == Conn.Connecting || shown == Conn.Retrying || shown == Conn.NoResponse;
            AppMode mode;
            IList<string> apps;
            CurrentRouting(out mode, out apps);
            // "Só estes" with Discord: always there while connected. Whole-PC modes: only during a call.
            bool visible = relevant && !busy && (discordLive || (on && mode == AppMode.Only));

            string state = "", detail = "", badge = "", tone = "Muted";
            bool far = false;
            if (visible && !on)
            {
                state = "Discord em chamada sem VPN";
                detail = "Conectar agora troca a rota e trava a chamada por alguns segundos.";
                tone = "Warn";
            }
            else if (visible)
            {
                LinkQuality.Rating rating = linkQuality.Rate();
                if (discordLive)
                {
                    state = settings.LiveGuard ? "Em chamada · Modo Live ativo" : "Em chamada pela VPN";
                    tone = "Secure";
                }
                else if (discordRunning) { state = "Discord pela VPN"; tone = "Secure"; }
                else state = "Discord fechado";

                if (qualityUnavailable) detail = "Túnel iniciado fora do app: sem medição de qualidade.";
                else if (linkQuality.Count >= 3) detail = linkQuality.Summary();
                else detail = discordRunning ? "Medindo a qualidade do túnel..." : "Quando abrir, ele já sai pela VPN.";
                if (discordLive && liveIncidents > 0)
                    detail += " · " + liveIncidents + (liveIncidents == 1 ? " falha" : " falhas");

                if (!qualityUnavailable && rating != LinkQuality.Rating.Unknown)
                {
                    badge = LinkQuality.RatingText(rating);
                    if (rating == LinkQuality.Rating.Bad) tone = "Danger";
                    else if (rating == LinkQuality.Rating.Unstable) tone = "Warn";
                    else if (linkQuality.AvgMs >= FarServerMs)
                    {
                        // steady but far away: every word and frame arrives late
                        far = true;
                        badge = "Distante";
                        tone = "Warn";
                    }
                }
            }

            string key = (visible ? "1" : "0") + "|" + state + "|" + detail + "|" + badge + "|" + tone;
            if (key == discordCardKey) return;
            discordCardKey = key;

            Visibility vis = visible ? Visibility.Visible : Visibility.Collapsed;
            if (discordCard.Visibility != vis)
            {
                discordCard.Visibility = vis;
                SyncIpNote();
                lastHomeAppsRunningKey = "";
                RenderHomeApps(); // the chip row gives way to the card, or comes back
            }
            if (!visible) return;

            if (!discordIconLoaded)
            {
                discordIconLoaded = true;
                ImageSource ico = null;
                try { ico = AppIcon("Discord", null); } catch { }
                imgDiscord.Source = ico;
                imgDiscord.Visibility = ico != null ? Visibility.Visible : Visibility.Collapsed;
            }
            txtDiscordState.Text = state;
            txtDiscordDetail.Text = detail;
            discordDot.Fill = B(tone);
            bool hasBadge = badge.Length > 0;
            discordBadge.Visibility = hasBadge ? Visibility.Visible : Visibility.Collapsed;
            if (hasBadge)
            {
                txtDiscordBadge.Text = badge;
                discordBadge.Background = B(tone == "Danger" ? "DangerSoft" : tone == "Warn" ? "WarnSoft" : "SecureSoft");
                txtDiscordBadge.Foreground = B(tone == "Danger" ? "Danger" : tone == "Warn" ? "Warn" : "SecureText");
            }
            discordCard.ToolTip = !on
                ? "O Discord está em chamada usando a sua internet normal."
                : "Ping · oscilação (jitter) · perda de pacotes, medidos por dentro do túnel a cada poucos segundos.\nOscilação e perda são o que mais afeta voz e transmissão. Clique para ver o histórico em Testes." +
                  (far ? "\n\nServidor distante: o ping alto atrasa a voz e a transmissão. Um servidor mais perto de você ajuda. " +
                         "Outra saída é pôr a região do canal de voz do Discord perto do servidor da VPN." : "");
        }

        // ---------------------------------------------------------------- best server for live

        void TestServersForLive()
        {
            if (liveTestRunning) return;
            List<ConfigInfo> list = ServersWithEndpoints(Paths.ConfigNames());
            if (list.Count == 0)
            {
                Dialogs.Info(w, "Nenhum servidor", "Adicione um servidor antes de testar.");
                return;
            }
            if (wasRunning == true && EffectiveAppMode() != AppMode.Only)
            {
                Dialogs.Info(w, "Desconecte para testar",
                    "Com a VPN ligada para todo o PC, o teste passaria por dentro do túnel e mediria o servidor atual, não os outros. Desconecte e teste de novo.");
                return;
            }

            var hosts = list.Select(c => c.Endpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var results = new Dictionary<string, Latency.Result>(StringComparer.OrdinalIgnoreCase);
            const int Workers = 3;
            int estimateSec = (int)Math.Ceiling(hosts.Count / (double)Math.Min(Workers, hosts.Count)) * 3 + 1;

            Window d = Dialogs.Frame(w, "Melhor servidor para live", 420);
            var body = new StackPanel();
            TextBlock status = Dialogs.Message("Testando " + hosts.Count + (hosts.Count == 1 ? " servidor" : " servidores") +
                " com 20 pacotes cada. Leva uns " + estimateSec + " segundos...");
            body.Children.Add(status);
            var rows = new StackPanel();
            body.Children.Add(new ScrollViewer
            {
                Content = rows,
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 12, 0, 0)
            });
            Button close = Dialogs.Btn("Fechar", "GhostButton");
            close.IsCancel = true;
            Button use = Dialogs.Btn("Usar", "PrimaryButton");
            use.Visibility = Visibility.Collapsed;
            use.Click += (s, e) => d.DialogResult = true;
            string best = null;
            bool closed = false;
            d.Closed += (s, e) => closed = true;

            liveTestRunning = true;
            btnLiveTest.IsEnabled = false;
            var ui = w.Dispatcher;
            new Thread(() =>
            {
                int next = -1;
                var workers = new List<Thread>();
                for (int k = 0; k < Math.Min(Workers, hosts.Count); k++)
                {
                    var t = new Thread(() =>
                    {
                        while (true)
                        {
                            int i = Interlocked.Increment(ref next);
                            if (i >= hosts.Count) break;
                            Latency.Result r;
                            try { r = Latency.ProbeStability(hosts[i]); }
                            catch { r = new Latency.Result(); }
                            lock (results) results[hosts[i]] = r;
                        }
                    }) { IsBackground = true };
                    workers.Add(t);
                    t.Start();
                }
                foreach (Thread t in workers) t.Join();
                ui.BeginInvoke(new Action(() =>
                {
                    liveTestRunning = false;
                    btnLiveTest.IsEnabled = true;
                    if (exiting || closed) return;
                    best = FillLiveTestResults(list, results, rows, status);
                    if (best != null && (active == null || !best.Equals(active.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        use.Content = "Usar " + (best.Length > 22 ? best.Substring(0, 21) + "…" : best);
                        use.Visibility = Visibility.Visible;
                    }
                }));
            }) { IsBackground = true }.Start();

            Dialogs.Layout(d, "Melhor servidor para live", body, close, use);
            bool accepted = d.ShowDialog() == true;
            if (accepted && best != null)
            {
                SwitchServer(best);
                if (WindowShowing) tabHome.IsChecked = true;
            }
        }

        // Fills the ranking and returns the name of the best server (null when none answered)
        string FillLiveTestResults(List<ConfigInfo> list, Dictionary<string, Latency.Result> results, StackPanel rows, TextBlock status)
        {
            var ranked = list
                .Select(c =>
                {
                    Latency.Result r;
                    results.TryGetValue(c.Endpoint, out r);
                    return new KeyValuePair<ConfigInfo, Latency.Result>(c, r ?? new Latency.Result());
                })
                .OrderBy(p => p.Value.LiveScore)
                .ThenBy(p => p.Key.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string best = ranked.Count > 0 && ranked[0].Value.Ok ? ranked[0].Key.Name : null;

            rows.Children.Clear();
            for (int i = 0; i < ranked.Count; i++)
            {
                ConfigInfo c = ranked[i].Key;
                Latency.Result r = ranked[i].Value;
                bool isBest = best != null && i == 0;
                bool isCurrent = active != null && c.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var rank = new TextBlock
                {
                    Text = r.Ok ? (i + 1).ToString() : "–",
                    Foreground = B(isBest ? "Accent" : "Muted"),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                name.Children.Add(new TextBlock
                {
                    Text = c.Name + (isCurrent ? " (atual)" : ""),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (isBest)
                    name.Children.Add(new TextBlock { Text = "Melhor para live", FontSize = 11, Foreground = B("Accent") });
                var metrics = new TextBlock
                {
                    Text = r.Ok ? r.Ms + " ms · osc. " + r.JitterMs + " ms · perda " + r.LossPct + "%" : "não responde a ping",
                    Foreground = B(!r.Ok ? "Muted" : r.LossPct >= 5 || r.JitterMs >= 30 ? "Warn" : "Text"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                Grid.SetColumn(metrics, 2);
                grid.Children.Add(rank);
                grid.Children.Add(name);
                grid.Children.Add(metrics);
                rows.Children.Add(new Border
                {
                    Background = isBest ? B("AccentSoft") : Brushes.Transparent,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 0, 0, 2),
                    Child = grid
                });
            }

            if (best == null)
                status.Text = "Nenhum servidor respondeu a ping (muitos bloqueiam), então não dá para comparar por aqui. " +
                              "Conecte em cada um e acompanhe o cartão do Discord no Início durante uma chamada.";
            else if (ranked.Count == 1)
                status.Text = "Você só tem um servidor. Adicione mais dois ou três do seu provedor (de preferência perto de você) para comparar: " +
                              "em chamadas e transmissões, oscilação e perda baixas valem mais que ping baixo.";
            else
                status.Text = "Do seu PC até cada servidor, em 20 pacotes. Em chamadas e transmissões, oscilação (osc.) e perda baixas valem mais que ping baixo.";
            return best;
        }
    }
}
