// Main window: connect, disconnect, auto-reconnect and failover.

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
        // What the user sees. "Connected" needs a WireGuard handshake, not just a running client.
        enum Conn { Off, Connecting, Retrying, On, NoResponse }

        const int WarnAfter = 15, GiveUpAfter = 60; // seconds without a handshake response

        Conn State
        {
            get
            {
                if (reconnectPending) return Conn.Connecting;
                // being stopped: the process lingers until the next poll
                if (wasRunning == true && userStopped) return Conn.Off;
                if (wasRunning == true)
                {
                    if (!monitoring) return Conn.On; // started outside the app: no log to check
                    if (!handshakeOk && pendingSinceTick != null && ElapsedSeconds(pendingSinceTick.Value) > WarnAfter) return Conn.NoResponse;
                    if (rehandshakeConfirmedDown) return Conn.NoResponse;
                    return handshakeOk ? Conn.On : Conn.Connecting;
                }
                if ((reconnectTimer != null && reconnectTimer.IsEnabled) || waitingForNetworkRestore || reconnectProbeRunning) return Conn.Retrying;
                return connecting ? Conn.Connecting : Conn.Off;
            }
        }

        void ResetAutoReconnectState()
        {
            reconnSeq++;
            waitingForNetworkRestore = false;
            reconnectProbeRunning = false;
            waitingForNetSinceTick = 0;
            if (reconnectTimer != null && reconnectTimer.IsEnabled) reconnectTimer.Stop();
        }

        void WakeAutoReconnectOnNetworkReturn()
        {
            if (exiting || userStopped || connecting || wasRunning == true) return;
            if (!waitingForNetworkRestore || reconnectProbeRunning) return;
            if (reconnectTimer != null &&
                (!reconnectTimer.IsEnabled || reconnectTimer.Interval > TimeSpan.FromMilliseconds(1600)))
            {
                reconnectTimer.Stop();
                reconnectTimer.Interval = TimeSpan.FromMilliseconds(1500);
                reconnectTimer.Start();
            }
        }

        void ScheduleNextAutoReconnect()
        {
            pendingPreConnectCleanup = true;
            if (reconnectTimer != null && reconnectTimer.IsEnabled) reconnectTimer.Stop();
            reconnectProbeRunning = true;
            int rSeq = ++reconnSeq;
            UpdateState();
            UpdatePollInterval();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                ConfFile.DeleteRuntimeCopies();
                ConfFile.FlushDnsCache();
                bool hasUp = NetDiag.HasPhysicalUplink();
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (rSeq != reconnSeq || exiting || userStopped || connecting || wasRunning == true)
                        {
                            if (rSeq == reconnSeq) reconnectProbeRunning = false;
                            return;
                        }
                        reconnectProbeRunning = false;
                        if (!hasUp)
                        {
                            waitingForNetworkRestore = true;
                            if (waitingForNetSinceTick == 0)
                            {
                                waitingForNetSinceTick = NowTicks();
                                NetMon.Record("Rede", NetMon.EventKind.Warn,
                                    "Sem rede física no momento — aguardando internet voltar para reconectar");
                            }
                            if (reconnectTimer != null)
                            {
                                reconnectTimer.Interval = TimeSpan.FromSeconds(5);
                                reconnectTimer.Start();
                            }
                        }
                        else
                        {
                            waitingForNetworkRestore = false;
                            waitingForNetSinceTick = 0;
                            int idx = Math.Min(reconnectAttempts, ReconnectDelaysSec.Length - 1);
                            int delaySec = ReconnectDelaysSec[idx];
                            reconnectAttempts++;
                            if (reconnectTimer != null)
                            {
                                reconnectTimer.Interval = TimeSpan.FromSeconds(delaySec);
                                reconnectTimer.Start();
                            }
                        }
                        UpdateState();
                        UpdatePollInterval();
                    }));
                }
                else if (rSeq == reconnSeq)
                {
                    reconnectProbeRunning = false;
                }
            });
        }

        static string FindFailoverServer(string currentName, ICollection<string> failedServers, AppMode? requiredMode)
        {
            try
            {
                string[] names = Paths.ConfigNames();
                if (names == null || names.Length <= 1) return null;
                List<ConfigInfo> all = ServersWithEndpoints(names);
                var candidates = new List<ConfigInfo>();
                for (int i = 0; i < all.Count; i++)
                {
                    ConfigInfo c = all[i];
                    if (c == null || string.IsNullOrEmpty(c.Endpoint)) continue;
                    if (!string.IsNullOrEmpty(currentName) && c.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (failedServers != null && failedServers.Contains(c.Name)) continue;
                    if (requiredMode.HasValue && c.Mode != requiredMode.Value) continue;
                    candidates.Add(c);
                }
                if (candidates.Count == 0) return null;

                // Snapshot cached composite Score (Ping + Jitter + Loss) before sorting
                var cachedScore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < candidates.Count; i++)
                {
                    Latency.Result r = Latency.Cached(candidates[i].Endpoint);
                    if (r != null && r.Ok) cachedScore[candidates[i].Name] = r.Score;
                }
                candidates.Sort((a, b) =>
                {
                    int sa, sb;
                    bool oka = cachedScore.TryGetValue(a.Name, out sa);
                    bool okb = cachedScore.TryGetValue(b.Name, out sb);
                    if (oka != okb) return oka ? -1 : 1;
                    if (oka && okb) return sa.CompareTo(sb);
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });

                string bestName = null;
                int bestMs = int.MaxValue;
                int maxChecks = Math.Min(candidates.Count, 4);
                for (int i = 0; i < maxChecks; i++)
                {
                    NetDiag.ProbeResult ep = NetDiag.ProbeEndpoint(candidates[i], 1000, CancellationToken.None);
                    if (ep != null && ep.Ok)
                    {
                        int ms = ep.LatencyMs > 0 ? ep.LatencyMs : 9999;
                        if (ms < bestMs)
                        {
                            bestMs = ms;
                            bestName = candidates[i].Name;
                        }
                    }
                }
                return bestName;
            }
            catch { }
            return null;
        }

        void ExecuteReconnectTimerTick()
        {
            if (exiting) return;
            if (userStopped || wasRunning == true)
            {
                ResetAutoReconnectState();
                UpdateState();
                UpdatePollInterval();
                return;
            }
            if (connecting) return;

            bool wasWaiting = waitingForNetworkRestore;
            int attemptNow = reconnectAttempts;
            string curName = active != null ? active.Name : null;
            AppMode? requiredMode = (!settings.SharedApps && active != null) ? (AppMode?)active.Mode : null;
            List<string> failedSnap = new List<string>(failedServersThisDrop);
            reconnectProbeRunning = true;
            int rSeq = ++reconnSeq;
            UpdateState();
            UpdatePollInterval();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool hasUp = NetDiag.HasPhysicalUplink();
                string failoverTarget = null;
                if (!hasUp)
                {
                    ConfFile.FlushDnsCache();
                }
                else if (attemptNow >= 3)
                {
                    var excluded = new HashSet<string>(failedSnap, StringComparer.OrdinalIgnoreCase);
                    failoverTarget = FindFailoverServer(curName, excluded, requiredMode);
                }

                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (rSeq != reconnSeq || exiting)
                        {
                            if (rSeq == reconnSeq) reconnectProbeRunning = false;
                            return;
                        }
                        if (userStopped || wasRunning == true)
                        {
                            ResetAutoReconnectState();
                            UpdateState();
                            UpdatePollInterval();
                            return;
                        }
                        if (connecting)
                        {
                            reconnectProbeRunning = false;
                            return;
                        }
                        reconnectProbeRunning = false;

                        if (!hasUp)
                        {
                            if (!wasWaiting && reconnectAttempts > 0) reconnectAttempts--;
                            waitingForNetworkRestore = true;
                            if (waitingForNetSinceTick == 0)
                            {
                                waitingForNetSinceTick = NowTicks();
                                NetMon.Record("Rede", NetMon.EventKind.Warn,
                                    "Sem rede física — aguardando conexão voltar para reconectar");
                            }
                            if (ElapsedSeconds(waitingForNetSinceTick) >= 300.0)
                            {
                                ResetAutoReconnectState();
                                QueuePostDropCleanup(true);
                                UpdateState();
                                UpdatePollInterval();
                                ShowError("A conexão caiu", "A rede física ficou sem sinal por mais de 5 minutos.");
                                return;
                            }
                            if (reconnectTimer != null)
                            {
                                reconnectTimer.Interval = TimeSpan.FromSeconds(5);
                                reconnectTimer.Start();
                            }
                            UpdateState();
                            UpdatePollInterval();
                            return;
                        }

                        waitingForNetworkRestore = false;
                        waitingForNetSinceTick = 0;
                        if (reconnectAttempts == 0) reconnectAttempts = 1;

                        if (!string.IsNullOrEmpty(failoverTarget) && File.Exists(Paths.Conf(failoverTarget)))
                        {
                            if (string.IsNullOrEmpty(settings.PreferredConfigOverride) && !string.IsNullOrEmpty(curName))
                                settings.PreferredConfigOverride = curName;
                            if (!string.IsNullOrEmpty(curName)) failedServersThisDrop.Add(curName);
                            NetMon.Record("Túnel", NetMon.EventKind.Warn,
                                "Failover automático temporário: '" + (curName ?? "atual") + "' instável, tentando '" + failoverTarget + "'");
                            settings.Config = failoverTarget;
                            RefreshConfigs();
                            LoadAppsEditor();
                            if (!WindowShowing)
                                Balloon("Servidor anterior sem resposta. Reconectando via " + failoverTarget + "...");
                        }

                        Connect(true);
                    }));
                }
                else if (rSeq == reconnSeq)
                {
                    reconnectProbeRunning = false;
                }
            });
        }

        // The kill switch only exists in "Todos" mode (Tunnel.Start turns it off for split tunneling)
        bool EffectiveNetLock { get { return settings.NetworkLock && EffectiveAppMode() == AppMode.All; } }

        void QueuePostDropCleanup(bool unexpectedDrop)
        {
            int dropSeq = ++postDropCleanupSeq;
            bool netLockSnap = EffectiveNetLock;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                AutoHealer.TryPostDropRecovery(netLockSnap, unexpectedDrop,
                    () => exiting || dropSeq != postDropCleanupSeq || connecting || reconnectPending || wasRunning == true);
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null && !exiting)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (exiting || dropSeq != postDropCleanupSeq) return;
                        ipTimer.Stop();
                        ipTimer.Start();
                    }));
                }
            });
        }

        void Toggle()
        {
            if ((reconnectTimer != null && reconnectTimer.IsEnabled) || waitingForNetworkRestore || reconnectProbeRunning)
            {
                ResetAutoReconnectState();
                reconnectAttempts = 0;
                bool needCleanup = pendingPreConnectCleanup;
                pendingPreConnectCleanup = false;
                NetMon.Record("Túnel", NetMon.EventKind.Info, "Reconexão automática cancelada pelo usuário");
                UpdateState();
                UpdatePollInterval();
                if (needCleanup) QueuePostDropCleanup(false);
                return;
            }
            if ((wasRunning == true && !userStopped) || connecting || reconnectPending)
            {
                if (!ConfirmLiveInterruption("Desligar a VPN durante a chamada?",
                        "Desligar agora trava a chamada ou a transmissão por alguns segundos, e o Discord passa a usar o seu IP real.", "Desligar"))
                    return;
                Disconnect(true);
            }
            else Connect();
        }

        void Connect() { Connect(false); }

        void Connect(bool fromAutoReconnect)
        {
            HideBanner();
            if (!Ready)
            {
                ShowWindow();
                tabHome.IsChecked = true;
                ShowPage(HomePage);
                return;
            }
            if (diagRepairRunning)
            {
                if (fromAutoReconnect)
                {
                    if (!userStopped && wasRunning != true && !reconnectPending && reconnectTimer != null && !reconnectTimer.IsEnabled)
                    {
                        reconnectTimer.Interval = TimeSpan.FromSeconds(3);
                        reconnectTimer.Start();
                    }
                }
                else if (WindowShowing)
                {
                    Dialogs.Info(w, "Reparo em andamento", "Aguarde a conclusão da correção na aba Testes antes de conectar.");
                }
                UpdateState();
                return;
            }
            ResetAutoReconnectState();
            reconnectPending = false;
            scheduleReconnectAfterStop = false;
            if (connecting || (wasRunning == true && !userStopped))
            {
                UpdateState();
                return;
            }
            StopContinuousDiag("Interrompido: túnel alterado");

            string cfgName = active.Name;
            string wireSockExe = Paths.WireSock;
            AppMode targetMode = settings.AppsMode != AppMode.All && settings.Apps.Count == 0 ? AppMode.All : settings.AppsMode;
            AppMode effectiveMode = settings.SharedApps ? targetMode : active.Mode;
            bool netLock = settings.NetworkLock && effectiveMode == AppMode.All;
            bool updateApps = settings.SharedApps && !SameApps(active, settings.AppsMode, settings.Apps);
            bool updateMtu = (settings.OptimizeMtu && active.Mtu == 0) || (!settings.OptimizeMtu && active.AutoMtu);
            List<string> targetApps = targetMode == AppMode.All ? new List<string>() : new List<string>(settings.Apps);
            int targetMtu = settings.OptimizeMtu ? 1380 : 0;

            if (!fromAutoReconnect)
            {
                reconnectAttempts = 0;
                failedServersThisDrop.Clear();
            }
            bool needPreCleanup = pendingPreConnectCleanup;
            pendingPreConnectCleanup = false;
            postDropCleanupSeq++;
            bgMtuReducedThisSession = false;
            mtuLogBurstCount = 0;
            mtuLogWindowStartTick = 0;
            CloseLogStream();
            Traffic.Stop();
            ResetTrafficLabels();
            userStopped = false;
            connecting = true;
            connectPausedForDialog = false;
            connectLaunchTick = 0;
            connectTotalStartTick = NowTicks();
            StartMonitoring(true);
            appsNeedReconnect = false;
            NetMon.Record("Túnel", NetMon.EventKind.Info, (fromAutoReconnect ? "Reconectando " : "Conectando ") + cfgName + " (" + effectiveMode + ")");
            settings.AddRecent(cfgName);
            UpdateAppsSaved();
            UpdateState();
            UpdatePollInterval();

            bool autoHeal = settings.AutoHeal;
            bool optMtuSnap = settings.OptimizeMtu;
            int maxMtuSnap = EffectiveMaxMtu();
            bool gameStabilitySnap = settings.GameStabilityMode;
            int keepaliveSnap = gameStabilitySnap ? 15 : 0;
            string customDnsSnap = settings.CustomDns;
            bool needPreMtuCheck = optMtuSnap && gameStabilitySnap && settings.AdaptiveMtu == 0 && (maxMtuSnap == 0 || maxMtuSnap >= 1380);
            string epSnap = active != null ? active.Endpoint : null;
            VpnEnginePreference enginePrefSnap = settings.EnginePreference;
            TunnelEngine resolvedEngine = Tunnel.ResolveEngine(cfgName, effectiveMode, enginePrefSnap);
            int seq = ++connectSeq;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool markedConnecting = false;
                try
                {
                    if (needPreCleanup)
                    {
                        if (Tunnel.HasPendingNetworkLock || netLock) Tunnel.ResetNetworkLock();
                        else ConfFile.CleanGhostNetwork();
                        if (seq != connectSeq) return;
                    }
                    if (needPreMtuCheck && NetDiag.HasPhysicalUplink())
                    {
                        string netSigNow;
                        lock (netSigLock) netSigNow = lastNetSignature ?? "";
                        int calibrated = 0;
                        bool cacheHit = false;
                        lock (mtuCacheLock)
                        {
                            if (hasCachedPreMtu && cachedMtuNetSig == netSigNow &&
                                (DateTime.UtcNow - cachedPreMtuAtUtc).TotalMinutes < 10.0)
                            {
                                calibrated = cachedPreMtu;
                                cacheHit = true;
                            }
                        }
                        if (!cacheHit)
                        {
                            Interlocked.Exchange(ref suppressMtuLogUntilTick, FutureTicks(10.0));
                            bool epIsV6 = !string.IsNullOrEmpty(epSnap) && epSnap.Contains(":");
                            if (!epIsV6 && !string.IsNullOrEmpty(epSnap))
                            {
                                IPAddress parsedEp;
                                if (IPAddress.TryParse(epSnap, out parsedEp))
                                    epIsV6 = parsedEp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                            }
                            NetDiag.MtuResult mtuPre = NetDiag.ProbeMtu(IPAddress.Parse("1.1.1.1"), 650, true, CancellationToken.None);
                            if (mtuPre != null && mtuPre.Ok && mtuPre.MaxPayload > 0)
                            {
                                calibrated = NetDiag.RecommendSafeWireGuardMtu(mtuPre.MaxPayload, epIsV6);
                                if (calibrated > 0 && calibrated < (maxMtuSnap > 0 ? maxMtuSnap : 1380))
                                {
                                    NetMon.Record("MTU", NetMon.EventKind.Info,
                                        "Pré-calibração de MTU aplicou " + calibrated + " B (limite físico " + (mtuPre.MaxPayload + 28) + " B)");
                                }
                            }
                            else
                            {
                                calibrated = -1; // cache ICMP-blocked/failed network too so reconnects don't re-stall
                            }
                            lock (mtuCacheLock)
                            {
                                hasCachedPreMtu = true;
                                cachedMtuNetSig = netSigNow;
                                cachedPreMtu = calibrated;
                                cachedPreMtuAtUtc = DateTime.UtcNow;
                            }
                            Interlocked.Exchange(ref suppressMtuLogUntilTick, FutureTicks(1.0));
                        }
                        if (calibrated > 0 && (maxMtuSnap == 0 || calibrated < maxMtuSnap))
                            maxMtuSnap = calibrated;
                        if (seq != connectSeq) return;
                    }
                    if (autoHeal)
                    {
                        AutoHealer.TryBackgroundAutoHeal(autoHeal, optMtuSnap, maxMtuSnap, false, false, false, null, () => exiting || seq != connectSeq);
                        if (seq != connectSeq) return;
                    }
                    lock (connectFlagLock)
                    {
                        if (seq != connectSeq) return;
                        activeConnectWorkers++;
                        Tunnel.Connecting = true;
                        markedConnecting = true;
                    }
                    if (Paths.WireSockServiceRunning)
                    {
                        bool confirmed = false;
                        Exception stopEx = null;
                        connectPausedForDialog = true;
                        try
                        {
                            w.Dispatcher.Invoke(new Action(() =>
                            {
                                if (seq != connectSeq) return;
                                ShowWindow();
                                confirmed = Dialogs.Confirm(w, "O WireSock já está conectado",
                                    "O aplicativo do WireSock está com uma conexão ativa. Para conectar pelo VPN Panel, essa conexão precisa ser desligada.",
                                    "Desligar e conectar", false);
                            }));
                        }
                        finally { connectPausedForDialog = false; }
                        if (seq != connectSeq) return;
                        if (!confirmed)
                        {
                            w.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (seq != connectSeq) return;
                                connecting = false;
                                connectLaunchTick = 0;
                                StopMonitoring();
                                UpdateState();
                                UpdatePollInterval();
                            }));
                            return;
                        }
                        try { Paths.StopWireSockService(); }
                        catch (Exception ex) { stopEx = ex; }
                        if (stopEx != null)
                        {
                            w.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (seq != connectSeq) return;
                                connecting = false;
                                connectLaunchTick = 0;
                                StopMonitoring();
                                UpdateState();
                                UpdatePollInterval();
                                Dialogs.Info(w, "Não foi possível desligar", "Desconecte pelo aplicativo do WireSock e tente de novo.\n\n" + stopEx.Message);
                            }));
                            return;
                        }
                    }

                    if (seq != connectSeq) return;
                    if (updateApps || updateMtu)
                    {
                        try
                        {
                            ConfFile.Rewrite(Paths.Conf(cfgName), updateApps, targetMode, targetApps, updateMtu, targetMtu);
                            ConfigInfo updated = ConfigInfo.Read(cfgName);
                            w.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (active != null && active.Name.Equals(cfgName, StringComparison.OrdinalIgnoreCase))
                                    active = updated;
                            }));
                        }
                        catch (Exception ex)
                        {
                            if (updateApps)
                            {
                                Exception appEx = ex;
                                w.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (seq != connectSeq) return;
                                    connecting = false;
                                    connectLaunchTick = 0;
                                    StopMonitoring();
                                    UpdateState();
                                    UpdatePollInterval();
                                    Dialogs.Info(w, "Não foi possível aplicar os apps", appEx.Message);
                                }));
                                return;
                            }
                        }
                    }

                    if (seq != connectSeq) return;
                    try
                    {
                        Tunnel.Start(wireSockExe, cfgName, netLock, maxMtuSnap, keepaliveSnap, customDnsSnap, enginePrefSnap);
                        w.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (seq != connectSeq) return;
                            SyncMtuUiFromSettings();
                            settings.Save();
                            connectLaunchTick = NowTicks();
                            Poll();
                        }));
                    }
                    catch (Exception ex)
                    {
                        Exception startEx = ex;
                        w.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (seq != connectSeq) return;
                            connecting = false;
                            connectLaunchTick = 0;
                            StopMonitoring();
                            UpdateState();
                            UpdatePollInterval();
                            ShowError(resolvedEngine == TunnelEngine.Wintun ? "Não foi possível iniciar o WireGuard (Wintun)" : "Não foi possível iniciar o WireSock", startEx.Message);
                        }));
                    }
                }
                finally
                {
                    if (markedConnecting)
                    {
                        lock (connectFlagLock)
                        {
                            if (--activeConnectWorkers == 0)
                                Tunnel.Connecting = false;
                        }
                    }
                }
            });
        }

        // byUser: the user asked for it (resets the auto-reconnect budget)
        void Disconnect(bool byUser)
        {
            StopContinuousDiag("Interrompido: túnel alterado");
            int seq = ++connectSeq;
            postDropCleanupSeq++;
            userStopped = true;
            connecting = false;
            connectPausedForDialog = false;
            connectLaunchTick = 0;
            wasRunning = false;
            runningConfig = null;
            StopMonitoring();
            Traffic.Stop();
            ResetTrafficLabels();
            NetMon.Record("Túnel", NetMon.EventKind.Info, byUser ? "Desconectado pelo usuário" : "Desconectando túnel");
            if (!reconnectPending) pendingPostHealDiagNote = null;
            if (byUser)
            {
                pendingPostHealDiagNote = null;
                reconnectPending = false;
                scheduleReconnectAfterStop = false;
                pendingPreConnectCleanup = false;
                reconnectAttempts = 0;
                failedServersThisDrop.Clear();
                ResetAutoReconnectState();
            }
            bool netLock = EffectiveNetLock;
            bool autoHealSnap = settings.AutoHeal;
            bool willReconnect = reconnectPending || scheduleReconnectAfterStop;
            UpdateState();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tunnel.Stop(netLock);
                if (!willReconnect && autoHealSnap && !exiting && !Tunnel.Connecting && !Tunnel.Active(true))
                {
                    List<string> restoredDns;
                    AutoHealer.RestoreStuckPhysicalDns(out restoredDns);
                    ConfFile.FlushDnsCache();
                }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        ipTimer.Stop();
                        ipTimer.Start();
                        UpdatePollInterval();
                        if (seq != connectSeq) return;
                        if (reconnectPending)
                        {
                            reconnectPending = false;
                            userStopped = false;
                            Connect(true);
                        }
                        else if (scheduleReconnectAfterStop)
                        {
                            scheduleReconnectAfterStop = false;
                            userStopped = false;
                            ScheduleNextAutoReconnect();
                        }
                        else
                        {
                            Poll();
                        }
                    }));
                }
            });
            UpdatePollInterval();
        }

        void Reconnect()
        {
            if (wasRunning != true && !connecting) { Connect(); return; }
            NetMon.Record("Túnel", NetMon.EventKind.Warn, "Reiniciando túnel...");
            reconnectPending = true;
            Disconnect(false);
        }
    }
}
