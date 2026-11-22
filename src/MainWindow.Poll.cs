// Main window: the 1-second poll and the handshake polls.

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
        void Poll()
        {
            if (!wireSockInstalled && !wireSockScanRunning && (lastWireSockBgCheckTick == 0 || ElapsedSeconds(lastWireSockBgCheckTick) >= 5))
            {
                wireSockScanRunning = true;
                lastWireSockBgCheckTick = NowTicks();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool found = Paths.WireSock != null;
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null)
                    {
                        ui.BeginInvoke(new Action(() =>
                        {
                            wireSockScanRunning = false;
                            if (exiting) return;
                            if (found != wireSockInstalled)
                            {
                                wireSockInstalled = found;
                                UpdateAbout();
                                UpdateSetup();
                            }
                        }));
                    }
                    else wireSockScanRunning = false;
                });
            }

            double scanInterval = WindowShowing ? 2.5 : 8.5;
            bool allowScan = wasRunning == true || connecting || reconnectPending || wasRunning == null ||
                             (DateTime.UtcNow - lastProcScan).TotalSeconds >= scanInterval;
            if (allowScan && wasRunning != true) lastProcScan = DateTime.UtcNow;
            bool running = Tunnel.Active(allowScan);

            if (running != wasRunning)
            {
                if (running)
                {
                    startedAt = Tunnel.StartTime;
                    connecting = false;
                    connectLaunchTick = 0;
                    // Fast path: if this instance just launched the tunnel, skip WMI
                    if (Tunnel.LastStartedConfig != null && (DateTime.UtcNow - Tunnel.LastStartedAt).TotalSeconds < 15)
                    {
                        runningConfig = Tunnel.LastStartedConfig;
                    }
                    else if (Tunnel.ActiveEngine == TunnelEngine.Wintun)
                    {
                        if (!cmdLineLookupRunning)
                        {
                            cmdLineLookupRunning = true;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string wCfg = Tunnel.ReadWintunActiveConfig();
                                DateTime wStart = Tunnel.FetchWintunServiceStartTime();
                                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                                if (ui != null)
                                {
                                    ui.BeginInvoke(new Action(() =>
                                    {
                                        cmdLineLookupRunning = false;
                                        if (exiting || wasRunning != true || Tunnel.ActiveEngine != TunnelEngine.Wintun) return;
                                        runningConfig = wCfg;
                                        if (wStart != default(DateTime)) startedAt = wStart;
                                        if (!monitoring) StartMonitoring(false);
                                        if (runningConfig != null && (active == null || active.Name != runningConfig) && File.Exists(Paths.Conf(runningConfig)))
                                        {
                                            settings.Config = runningConfig;
                                            RefreshConfigs();
                                            LoadAppsEditor();
                                        }
                                    }));
                                }
                                else cmdLineLookupRunning = false;
                            });
                        }
                    }
                    else
                    {
                        int pid = Tunnel.Pid;
                        DateTime procStarted = startedAt;
                        if (pid != 0 && !cmdLineLookupRunning)
                        {
                            cmdLineLookupRunning = true;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string cmd = Tunnel.CommandLine(pid);
                                string cmdCfg = Tunnel.ConfigOf(cmd);
                                bool hasRuntimeProbe = !string.IsNullOrEmpty(cmdCfg) && NetDiag.RuntimeHasProbeApp(cmdCfg);
                                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                                if (ui != null)
                                {
                                    ui.BeginInvoke(new Action(() =>
                                    {
                                        cmdLineLookupRunning = false;
                                        if (exiting || wasRunning != true || Tunnel.Pid != pid)
                                        {
                                            startupIsolationCheckPending = false;
                                            return;
                                        }
                                        Tunnel.NoteCommandLine(cmd, hasRuntimeProbe);
                                        runningConfig = cmdCfg;
                                        // a tunnel this app started before it was reopened still logs to our file
                                        if (!monitoring && cmd != null && cmd.Contains("-log-level info") && File.Exists(Paths.Log) &&
                                            File.GetLastWriteTime(Paths.Log) >= procStarted.AddSeconds(-5))
                                            StartMonitoring(false);
                                        if (runningConfig != null && (active == null || active.Name != runningConfig) && File.Exists(Paths.Conf(runningConfig)))
                                        {
                                            settings.Config = runningConfig;
                                            RefreshConfigs();
                                            LoadAppsEditor();
                                        }
                                        else if (settings.SharedApps && active != null && IsLive(active.Name))
                                        {
                                            appsNeedReconnect = !SameApps(active, settings.AppsMode, settings.Apps);
                                            UpdateAppsSaved();
                                        }
                                        if (startupIsolationCheckPending) CheckStartupIsolationAsync();
                                    }));
                                }
                                else cmdLineLookupRunning = false;
                            });
                        }
                    }
                    // follow tunnels started elsewhere (e.g. a .bat file)
                    if (runningConfig != null && (active == null || active.Name != runningConfig) && File.Exists(Paths.Conf(runningConfig)))
                    {
                        settings.Config = runningConfig;
                        RefreshConfigs();
                        LoadAppsEditor();
                    }
                }
                else if (wasRunning == true)
                {
                    bool quick = (DateTime.Now - startedAt).TotalSeconds < 10;
                    bool hadHandshake = sawHandshake;
                    StopMonitoring();
                    Traffic.Stop();
                    if (!userStopped && !reconnectPending)
                    {
                        lastDropTick = NowTicks();
                        if (Tunnel.LastActiveEngine == TunnelEngine.Wintun)
                            NetMon.Record("WireGuard", NetMon.EventKind.Error, "serviço Wintun encerrou inesperadamente");
                        else
                            NetMon.Record("WireSock", NetMon.EventKind.Error,
                                "wiresock-client encerrou inesperadamente (código " + Tunnel.LastExitCode + ")");
                        string droppedName = !string.IsNullOrEmpty(runningConfig) ? runningConfig : (active != null ? active.Name : null);
                        if (!string.IsNullOrEmpty(droppedName)) failedServersThisDrop.Add(droppedName);
                        bool willAutoReconnect = settings.AutoReconnect && reconnectAttempts < MaxReconnects &&
                                                 (!quick || hadHandshake || reconnectAttempts > 0);
                        if (willAutoReconnect)
                        {
                            ScheduleNextAutoReconnect();
                        }
                        else
                        {
                            QueuePostDropCleanup(true);
                            if (quick && reconnectAttempts == 0) ShowError("Não foi possível conectar", LogTail());
                            else ShowError("A conexão caiu", reconnectAttempts > 0 ? "Tentei reconectar " + reconnectAttempts + " vezes sem sucesso. " + LogTail() : LogTail());
                        }
                    }
                    else
                    {
                        ThreadPool.QueueUserWorkItem(_ => ConfFile.DeleteRuntimeCopies());
                    }
                    userStopped = false;
                    runningConfig = null;
                }
                wasRunning = running;
                // the route to every server changed: old pings no longer apply
                Latency.RouteChanged();
                UpdateAllPingLabels();
                // connected: measured once the handshake completes (Conn.On below), not through a half-open tunnel
                if (!running) MeasureLatency(false);
                // a tunnel not started by Connect (other instance, .bat) may run an older shared list
                if (running && settings.SharedApps && active != null && IsLive(active.Name))
                    appsNeedReconnect = !SameApps(active, settings.AppsMode, settings.Apps);
                if (!running && reconnectPending)
                {
                    reconnectPending = false;
                    Connect(true);
                }
                else if (!running && scheduleReconnectAfterStop)
                {
                    scheduleReconnectAfterStop = false;
                    ScheduleNextAutoReconnect();
                }
                else
                {
                    RefreshConfigs();
                }
            }

            if (running && monitoring)
            {
                if (Tunnel.ActiveEngine == TunnelEngine.Wintun) PollWintunHandshakeAsync();
                else ReadHandshakes();
                // a WireSock version that logs handshakes differently or whose 4 KB stdout buffer hasn't flushed yet:
                // assume initial connection succeeded after the grace window, keeping log monitoring active
                if (!sawHandshake && (DateTime.Now - startedAt).TotalSeconds > (logPos > 0 ? 10 : 25))
                {
                    // A connection this instance just started on WireSock is asked for proof (a probe through
                    // the tunnel) instead of being assumed; anything else keeps the old assumption.
                    if (Tunnel.ActiveEngine == TunnelEngine.Wintun || pendingSinceTick == null)
                    {
                        handshakeOk = true;
                        pendingSinceTick = null;
                    }
                    else if (!handshakeOk && !livenessRunning &&
                             (lastLivenessTick == 0 || ElapsedSeconds(lastLivenessTick) >= LivenessRetrySec))
                    {
                        RunLivenessProbe(true);
                    }
                }
                bool pastResumeGrace = resumeGraceUntilTick == 0 || NowTicks() >= resumeGraceUntilTick;
                // Established split tunnel ("Só estes"): check that traffic still crosses it
                if (handshakeOk && pastResumeGrace && !livenessRunning && !userStopped && !reconnectPending &&
                    Tunnel.ActiveEngine == TunnelEngine.WireSock && Tunnel.ProbeAppInjected &&
                    (lastLivenessTick == 0 || ElapsedSeconds(lastLivenessTick) >= (livenessFails > 0 ? LivenessRetrySec : LivenessEverySec)))
                {
                    RunLivenessProbe(false);
                }
                // Pre-handshake initial connection timeout (monotonic clock, paused on sleep)
                if (!handshakeOk && pendingSinceTick != null && pastResumeGrace && ElapsedSeconds(pendingSinceTick.Value) > GiveUpAfter)
                {
                    if (reconnectAttempts > 0 && settings.AutoReconnect && reconnectAttempts < MaxReconnects)
                    {
                        scheduleReconnectAfterStop = true;
                        Disconnect(false);
                    }
                    else
                    {
                        Disconnect(false);
                        bool hasMultiple = Paths.ConfigNames().Length > 1;
                        ShowBanner(false, "O servidor parou de responder",
                            "O handshake não completou a tempo. Tente reconectar ou troque para outro servidor.",
                            hasMultiple ? "Ir para o mais rápido" : "Tentar de novo",
                            hasMultiple ? (Action)ConnectFastest : Connect,
                            "Trocar servidor", () => tabServers.IsChecked = true);
                    }
                }
                // Post-handshake: verify with mode-appropriate probe if WireSock logged "Handshake did not complete"
                else if (handshakeOk && rehandshakeSuspectTick > 0 && pastResumeGrace)
                {
                    AppMode curMode = EffectiveAppMode();
                    if (curMode == AppMode.Only)
                    {
                        if (!rehandshakeOnlyChecked10s && !rehandshakeConfirmedDown && !activeProbeRunning && ElapsedSeconds(rehandshakeSuspectTick) > 10)
                        {
                            ProbeRehandshakeAsync(active);
                        }
                        else if (rehandshakeOnlyChecked10s && !rehandshakeConfirmedDown && !activeProbeRunning && ElapsedSeconds(rehandshakeSuspectTick) > 30)
                        {
                            rehandshakeConfirmedDown = true;
                            NetMon.Record("WireSock", NetMon.EventKind.Warn, "Re-handshake sem resposta há 30s (modo Só estes)");
                        }
                    }
                    else if (!rehandshakeConfirmedDown && !activeProbeRunning && ElapsedSeconds(rehandshakeSuspectTick) > 10)
                    {
                        ProbeRehandshakeAsync(active);
                    }

                    if (rehandshakeConfirmedDown && ElapsedSeconds(rehandshakeSuspectTick) > GiveUpAfter)
                        HandleTunnelConfirmedDown();
                }
            }

            // the client exited before a poll saw it (skip while user reads modal or service stops)
            if (!running && connecting && !connectPausedForDialog)
            {
                bool launchTimedOut = connectLaunchTick > 0 && ElapsedSeconds(connectLaunchTick) > 8;
                bool totalTimedOut = ElapsedSeconds(connectTotalStartTick) > 35;
                if (launchTimedOut || totalTimedOut)
                {
                    connecting = false;
                    connectLaunchTick = 0;
                    if (active != null && !string.IsNullOrEmpty(active.Name)) failedServersThisDrop.Add(active.Name);
                    if (settings.AutoReconnect && reconnectAttempts > 0 && reconnectAttempts < MaxReconnects)
                    {
                        ScheduleNextAutoReconnect();
                    }
                    else
                    {
                        QueuePostDropCleanup(true);
                        ShowError("Não foi possível conectar", LogTail());
                    }
                }
            }

            Conn now = State;
            if (now != shown)
            {
                if (now == Conn.NoResponse)
                    ShowBanner(true, "O servidor não está respondendo",
                        EffectiveNetLock ? "Continuo tentando. Enquanto isso, o kill switch mantém a internet bloqueada."
                                             : "Continuo tentando. Se não voltar em instantes, escolha outro servidor.",
                        "Trocar servidor", () => tabServers.IsChecked = true, "Desconectar", () => Disconnect(true));
                else if (shown == Conn.NoResponse && errorBanner.Visibility == Visibility.Visible && now != Conn.Off) HideBanner();
                UpdateState();
            }

            if (running)
            {
                UpdateTraffic();
                if (WindowShowing) UpdateUptimeText();
                // Only reset auto-reconnect budget after sustained stability (5 min)
                if ((DateTime.Now - startedAt).TotalSeconds > 300 && (lastDropTick == 0 || ElapsedSeconds(lastDropTick) > 300) && now == Conn.On)
                {
                    reconnectAttempts = 0;
                    failedServersThisDrop.Clear();
                }
                if (now == Conn.On && !string.IsNullOrEmpty(pendingPostHealDiagNote))
                {
                    string note = pendingPostHealDiagNote;
                    pendingPostHealDiagNote = null;
                    if (WindowShowing && pageDiag != null && pageDiag.Visibility == Visibility.Visible)
                        RunQuickDiag(note);
                }
            }
            else if (!connecting && !reconnectPending && !reconnectTimer.IsEnabled && !HasActiveOrPendingTunnel && !diagRepairRunning)
            {
                pendingPostHealDiagNote = null;
                if (settings.AutoHeal && Tunnel.HasPendingNetworkLock)
                    QueueBackgroundAutoHeal(false, false);
            }
            if (WindowShowing && pageHome.Visibility == Visibility.Visible)
            {
                if (!diagContinuousRunning && active != null && !string.IsNullOrEmpty(active.Endpoint) && (DateTime.UtcNow - lastHomePing).TotalSeconds >= 15)
                {
                    lastHomePing = DateTime.UtcNow;
                    Latency.Measure(new[] { active.Endpoint }, true, wasRunning == true);
                }
                if (now == Conn.On && active != null && active.Mode != AppMode.All && active.Apps.Count > 0 &&
                    (DateTime.UtcNow - lastHomeAppsCheck).TotalSeconds >= 3)
                {
                    lastHomeAppsCheck = DateTime.UtcNow;
                    RefreshRunningAppsAsync(active.Apps);
                }
            }
            if (WindowShowing && pageDiag != null && pageDiag.Visibility == Visibility.Visible)
                UpdateDiagTimeline();
            PollLive();
            UpdatePollInterval();
        }

        void PollWintunHandshakeAsync()
        {
            double minGap = !handshakeOk ? 1.2 : 6.0;
            if (wintunPollRunning || (lastWintunLogPollTick > 0 && ElapsedSeconds(lastWintunLogPollTick) < minGap)) return;
            wintunPollRunning = true;
            lastWintunLogPollTick = NowTicks();
            int seq = connectSeq;
            string activeCfgSnap = runningConfig ?? (active != null ? active.Name : null);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool wSaw, wOk, wFail;
                Tunnel.PollWintunStatus(handshakeOk, out wSaw, out wOk, out wFail);
                if (!wOk && !wFail && ConfFile.WintunRoutesInternet(activeCfgSnap))
                {
                    NetDiag.ProbeResult udp = NetDiag.ProbeUdpDns("1.1.1.1", 53, 800, CancellationToken.None);
                    if (udp != null && udp.Ok)
                    {
                        wSaw = true;
                        wOk = true;
                    }
                }

                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        wintunPollRunning = false;
                        if (exiting || seq != connectSeq || !monitoring || Tunnel.ActiveEngine != TunnelEngine.Wintun) return;
                        if (wOk)
                        {
                            bool recovering = rehandshakeSuspectTick > 0;
                            handshakeOk = sawHandshake = true;
                            pendingSinceTick = null;
                            rehandshakeSuspectTick = 0;
                            rehandshakeGen++;
                            rehandshakeConfirmedDown = false;
                            activeProbeRunning = false;
                            if (recovering)
                            {
                                NetMon.Record("WireGuard", NetMon.EventKind.Info, "Re-handshake Wintun recuperado");
                                ThreadPool.QueueUserWorkItem(__ => ConfFile.FlushDnsCache());
                            }
                        }
                        else if (wFail)
                        {
                            sawHandshake = true;
                            if (!handshakeOk)
                            {
                                if (pendingSinceTick == null) pendingSinceTick = NowTicks();
                            }
                            else if (rehandshakeSuspectTick == 0)
                            {
                                rehandshakeSuspectTick = NowTicks();
                                rehandshakeGen++;
                                NetMon.Record("WireGuard", NetMon.EventKind.Warn, "Re-handshake Wintun sem resposta — verificando túnel");
                            }
                        }
                        else if (wSaw)
                        {
                            sawHandshake = true;
                        }
                    }));
                }
                else wintunPollRunning = false;
            });
        }

        void ProbeRehandshakeAsync(ConfigInfo cfgSnapshot)
        {
            activeProbeRunning = true;
            AppMode effMode = EffectiveAppMode();
            int seq = connectSeq;
            int hsGen = rehandshakeGen;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool tunnelAlive = false;
                bool epOk = false;
                try
                {
                    if (effMode != AppMode.Only)
                    {
                        // All/Except: raw UDP DNS traverses the tunnel (no Windows DNS cache, no TCP RST false OK)
                        NetDiag.ProbeResult udpRes = NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, CancellationToken.None);
                        tunnelAlive = udpRes != null && udpRes.Ok;
                        if (!tunnelAlive && cfgSnapshot != null && !string.IsNullOrEmpty(cfgSnapshot.Endpoint))
                        {
                            NetDiag.ProbeResult epRes = NetDiag.ProbeEndpoint(cfgSnapshot, 1500, CancellationToken.None);
                            epOk = epRes != null && epRes.Ok;
                            if (seq == connectSeq && hsGen == rehandshakeGen)
                                NetMon.Record("Túnel", NetMon.EventKind.Warn,
                                    "Túnel sem resposta UDP; endpoint físico " + (epOk ? "acessível" : "inacessível"));
                        }
                    }
                    else
                    {
                        // Only mode: VpnPanelProbe runs inside AllowedApps when launched by VPN Panel;
                        // fall back to ProbeEndpoint when the tunnel was started externally without VpnPanelProbe.
                        bool hasHelper = Tunnel.ProbeAppInjected;
                        if (hasHelper)
                        {
                            NetDiag.ProbeResult tunRes = NetDiag.ProbeTunnelUdpViaHelper("1.1.1.1", 53, 1500, CancellationToken.None);
                            tunnelAlive = tunRes != null && tunRes.Ok;
                        }
                        if (!tunnelAlive && cfgSnapshot != null && !string.IsNullOrEmpty(cfgSnapshot.Endpoint))
                        {
                            NetDiag.ProbeResult epRes = NetDiag.ProbeEndpoint(cfgSnapshot, 1500, CancellationToken.None);
                            epOk = epRes != null && epRes.Ok;
                        }
                        else if (!tunnelAlive) epOk = true;
                    }
                }
                catch { tunnelAlive = false; epOk = false; }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (seq == connectSeq) activeProbeRunning = false;
                        if (exiting || seq != connectSeq || !monitoring || !handshakeOk || rehandshakeSuspectTick == 0 || hsGen != rehandshakeGen) return;
                        if (effMode == AppMode.Only)
                        {
                            if (tunnelAlive)
                            {
                                rehandshakeSuspectTick = 0;
                                rehandshakeGen++;
                                rehandshakeConfirmedDown = false;
                                rehandshakeOnlyChecked10s = false;
                                NetMon.Record("WireSock", NetMon.EventKind.Info, "Sonda no túnel Só estes respondeu — conexão preservada");
                                return;
                            }
                            rehandshakeOnlyChecked10s = true;
                            if (!epOk)
                            {
                                rehandshakeConfirmedDown = true;
                                NetMon.Record("Rede", NetMon.EventKind.Warn, "Re-handshake falhou e endpoint físico está inacessível");
                            }
                            return;
                        }
                        if (tunnelAlive)
                        {
                            rehandshakeSuspectTick = 0;
                            rehandshakeGen++;
                            rehandshakeConfirmedDown = false;
                        }
                        else
                        {
                            rehandshakeConfirmedDown = true;
                        }
                    }));
                }
                else if (seq == connectSeq) activeProbeRunning = false;
            });
        }
    }
}
