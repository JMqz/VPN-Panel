// Main window: handshake log monitoring, liveness probes and background auto-heal.

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
        void CloseLogStream()
        {
            if (logStream == null) return;
            try { logStream.Dispose(); } catch { }
            logStream = null;
        }

        void StopMonitoring()
        {
            monitoring = false;
            rehandshakeSuspectTick = 0;
            rehandshakeGen++;
            rehandshakeConfirmedDown = false;
            rehandshakeOnlyChecked10s = false;
            activeProbeRunning = false;
            ResetLiveness(true);
            ResetLinkQuality(true);
            CloseLogStream();
        }

        // Follows the WireSock log (written at -log-level info) for handshake events
        void StartMonitoring(bool fresh)
        {
            CloseLogStream();
            monitoring = true;
            handshakeOk = false;
            sawHandshake = false;
            seekToTail = !fresh;
            pendingSinceTick = fresh ? (long?)NowTicks() : null;
            rehandshakeSuspectTick = 0;
            rehandshakeGen++;
            rehandshakeConfirmedDown = false;
            rehandshakeOnlyChecked10s = false;
            activeProbeRunning = false;
            ResetLiveness(true);
            ResetLinkQuality(true);
            mtuLogBurstCount = 0;
            mtuLogWindowStartTick = 0;
            logPos = 0;
            logRest = "";
            logDecoder.Reset();
        }

        void ResetLiveness(bool dropSession)
        {
            livenessGen++;
            livenessRunning = false;
            livenessArmed = false;
            livenessFails = 0;
            graceProbeFails = 0;
            lastLivenessTick = 0;
            if (dropSession && livenessSession != null)
            {
                NetDiag.TunnelUdpSession old = livenessSession;
                livenessSession = null;
                ThreadPool.QueueUserWorkItem(_ => { try { old.Dispose(); } catch { } });
            }
        }

        // Is traffic really crossing the tunnel? true/false, or null when there is no way to tell
        // (no probe inside the tunnel, or the internet itself is down, which a reconnect would not fix).
        static bool? TestTunnelAlive(AppMode mode, NetDiag.TunnelUdpSession session)
        {
            if (mode == AppMode.Only)
            {
                // Only the listed apps are tunneled, so the question goes through VpnPanelProbe (added to
                // the runtime AllowedApps); the panel itself is outside the tunnel and works as the control.
                if (session == null || !session.IsReady) return null;
                NetDiag.ProbeResult r = session.Query("1.1.1.1", 53, 1500, CancellationToken.None);
                if (r != null && r.Ok) return true;
                if (!session.IsReady) return null;
                r = session.Query("8.8.8.8", 53, 1500, CancellationToken.None);
                if (r != null && r.Ok) return true;
                if (!session.IsReady) return null;
                if (!NetDiag.HasPhysicalUplink()) return null;
                NetDiag.ProbeResult direct = NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, CancellationToken.None);
                if (direct == null || !direct.Ok) return null;
                return false;
            }
            // Todos / Exceto: the panel's own traffic goes through the tunnel
            NetDiag.ProbeResult udp = NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, CancellationToken.None);
            if (udp != null && udp.Ok) return true;
            NetDiag.ProbeResult tcp = NetDiag.ProbeTcp("1.1.1.1", 443, 1500, CancellationToken.None);
            if (tcp != null && tcp.Ok) return true;
            if (!NetDiag.HasPhysicalUplink()) return null;
            return false;
        }

        // initial: the grace window ended with no handshake line in the log, so ask the tunnel itself
        // instead of assuming it works. Otherwise: periodic check of an established split tunnel.
        void RunLivenessProbe(bool initial)
        {
            if (livenessRunning) return;
            livenessRunning = true;
            lastLivenessTick = NowTicks();
            int gen = livenessGen;
            string cfgName = runningConfig ?? (active != null ? active.Name : null);
            AppMode mode = cfgName != null ? ConfigInfo.Read(cfgName).Mode : AppMode.All;
            bool useHelper = mode == AppMode.Only && Tunnel.ProbeAppInjected;
            NetDiag.TunnelUdpSession session = useHelper ? livenessSession : null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool? alive = null;
                NetDiag.TunnelUdpSession created = null;
                try
                {
                    if (useHelper && (session == null || !session.IsReady))
                    {
                        created = new NetDiag.TunnelUdpSession();
                        session = created;
                    }
                    alive = TestTunnelAlive(mode, session);
                }
                catch { alive = null; }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui == null)
                {
                    if (created != null) { try { created.Dispose(); } catch { } }
                    return;
                }
                ui.BeginInvoke(new Action(() =>
                {
                    bool current = gen == livenessGen && !exiting && monitoring && wasRunning == true;
                    if (created != null)
                    {
                        NetDiag.TunnelUdpSession stale = current ? livenessSession : created;
                        if (current) livenessSession = created;
                        if (stale != null) ThreadPool.QueueUserWorkItem(__ => { try { stale.Dispose(); } catch { } });
                    }
                    if (gen != livenessGen) return;
                    livenessRunning = false;
                    if (!current) return;
                    if (initial) OnInitialProbeResult(alive);
                    else OnLivenessResult(alive);
                }));
            });
        }

        void OnInitialProbeResult(bool? alive)
        {
            if (handshakeOk) return;
            if (alive == false)
            {
                // keep waiting: "Sem resposta" shows after WarnAfter and the connection is dropped after GiveUpAfter
                if (++graceProbeFails == 1)
                    NetMon.Record("Túnel", NetMon.EventKind.Warn, "Sem handshake no log e a sonda não atravessou o túnel — aguardando o servidor");
                return;
            }
            // true: proven. null: nothing to test with, so assume it works as older versions did
            // (a WireSock version that logs handshakes differently, or output still in its 4 KB buffer).
            handshakeOk = true;
            pendingSinceTick = null;
            if (alive == true)
            {
                livenessArmed = true;
                NetMon.Record("Túnel", NetMon.EventKind.Info, "Conexão confirmada por sonda no túnel (handshake ainda não apareceu no log)");
            }
        }

        void OnLivenessResult(bool? alive)
        {
            if (!handshakeOk || alive == null) return;
            if (alive == true)
            {
                livenessArmed = true;
                if (livenessFails > 0 || rehandshakeSuspectTick > 0)
                {
                    if (livenessFails > 0)
                        NetMon.Record("Túnel", NetMon.EventKind.Info, "Sonda no túnel voltou a responder — conexão preservada");
                    rehandshakeSuspectTick = 0;
                    rehandshakeGen++;
                    rehandshakeConfirmedDown = false;
                    rehandshakeOnlyChecked10s = false;
                }
                livenessFails = 0;
                return;
            }
            // Only judge a tunnel whose probe has already worked in this session: a provider that
            // filters the probe targets must never cause reconnect loops.
            if (!livenessArmed) return;
            livenessFails++;
            if (livenessFails == 1)
                NetMon.Record("Túnel", NetMon.EventKind.Warn, "Sonda no túnel sem resposta (internet direta OK) — verificando de novo");
            if (livenessFails == 2)
            {
                rehandshakeConfirmedDown = true; // shows "Sem resposta"
                if (rehandshakeSuspectTick == 0)
                {
                    rehandshakeSuspectTick = NowTicks();
                    rehandshakeGen++;
                }
            }
            if (livenessFails >= LivenessFailLimit)
            {
                if (TryHoldTunnelForLive()) return;
                livenessFails = 0;
                NetMon.Record("Túnel", NetMon.EventKind.Error, "Túnel parou de passar tráfego (" + LivenessFailLimit + " sondas seguidas sem resposta)");
                HandleTunnelConfirmedDown();
            }
        }

        // The tunnel process is alive but the server stopped answering
        void HandleTunnelConfirmedDown()
        {
            if (TryHoldTunnelForLive()) return;
            if (settings.AutoReconnect && reconnectAttempts < MaxReconnects)
            {
                reconnectAttempts++;
                lastDropTick = NowTicks();
                if (!WindowShowing)
                    Balloon("O servidor " + (active != null ? active.Name : "") + " parou de responder. Reconectando...");
                Reconnect();
            }
            else
            {
                Disconnect(false);
                bool hasMultiple = Paths.ConfigNames().Length > 1;
                ShowBanner(false, "O servidor parou de responder",
                    "O servidor parou de responder após a conexão. Tente reconectar ou troque para o mais rápido.",
                    hasMultiple ? "Ir para o mais rápido" : "Tentar de novo",
                    hasMultiple ? (Action)ConnectFastest : Connect,
                    "Trocar servidor", () => tabServers.IsChecked = true);
            }
        }

        static string ExtractLogMessage(string line, out string level)
        {
            level = "";
            if (string.IsNullOrEmpty(line)) return "";
            Match lm = Regex.Match(line, "\"log_level\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (lm.Success) level = lm.Groups[1].Value.Trim().ToLowerInvariant();
            Match mm = Regex.Match(line, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (mm.Success)
            {
                string msg = mm.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                return Regex.Replace(msg, @"\[\w+\]:\s*(Unhandled exception\s*)?", "").Trim();
            }
            return Regex.Replace(line.Trim(), @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s*", "").Trim();
        }

        void QueueBackgroundAutoHeal(bool tunnelRunning, bool repeatedMtuError)
        {
            if (exiting || !settings.AutoHeal) return;
            if (tunnelRunning && LiveNow)
            {
                // repairs can wait for the call to end; the next event after it queues them again
                if (!liveHealSkipNoted)
                {
                    liveHealSkipNoted = true;
                    RecordLive(NetMon.EventKind.Info, "Autocura adiada: chamada do Discord em andamento");
                }
                return;
            }
            if (repeatedMtuError)
            {
                if (!settings.OptimizeMtu || bgMtuReducedThisSession) return;
                if (bgMtuHealEnqueueTick > 0 && ElapsedSeconds(bgMtuHealEnqueueTick) < 60.0) return;
            }
            else
            {
                if (bgNetHealEnqueueTick > 0 && ElapsedSeconds(bgNetHealEnqueueTick) < 60.0) return;
            }
            if (Interlocked.CompareExchange(ref bgHealInFlight, 1, 0) != 0) return;

            if (repeatedMtuError) bgMtuHealEnqueueTick = NowTicks();
            else bgNetHealEnqueueTick = NowTicks();

            bool autoHealSnap = settings.AutoHeal;
            bool optMtuSnap = settings.OptimizeMtu;
            int curMaxMtuSnap = DiagEffectiveMtu(tunnelRunning, EffectiveAppMode(), settings);
            bool allowMtu = repeatedMtuError && !bgMtuReducedThisSession;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AutoHealer.FixReport hr = null;
                try
                {
                    hr = AutoHealer.TryBackgroundAutoHeal(
                        autoHealSnap,
                        optMtuSnap,
                        curMaxMtuSnap,
                        tunnelRunning,
                        repeatedMtuError,
                        allowMtu,
                        null,
                        () => exiting || Tunnel.Connecting || reconnectPending || (!tunnelRunning && wasRunning == true));
                }
                finally
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null)
                    {
                        ui.BeginInvoke(new Action(() =>
                        {
                            Interlocked.Exchange(ref bgHealInFlight, 0);
                            if (exiting) return;
                            ApplyHealReportToSettings(hr);
                            if (hr != null && hr.SuggestedMtu > 0) bgMtuReducedThisSession = true;
                            if (hr != null && hr.NeedReconnect && hr.SuggestedMtu > 0 && wasRunning == true)
                            {
                                // Never restart a working tunnel by itself: that would cut a call or a live
                                // stream for an optimization. The new MTU is saved and used on the next connection.
                                ShowBanner(true, "MTU adaptado para " + hr.SuggestedMtu + " bytes",
                                    "O log acusou pacotes acima do MTU. O ajuste vale na próxima conexão; reconecte quando puder (isso interrompe chamadas e transmissões por alguns segundos).",
                                    "Reconectar agora", ReconnectByUser, "Depois", HideBanner);
                            }
                            else if (hr != null && hr.FixedCount > 0)
                            {
                                UpdateState();
                                CheckIp();
                            }
                        }));
                    }
                    else Interlocked.Exchange(ref bgHealInFlight, 0);
                }
            });
        }

        void RecordWireSockLogEvent(string key, NetMon.EventKind kind, string message)
        {
            long lastTick;
            if (netMonLogCooldown.TryGetValue(key, out lastTick) && ElapsedSeconds(lastTick) < 10.0) return;
            netMonLogCooldown[key] = NowTicks();
            NetMon.Record("WireSock", kind, message);
        }

        void InspectHandshakeLine(string line, bool suppressEvents)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.IndexOf("Handshake response received", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Receiving handshake response", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool recovering = rehandshakeSuspectTick > 0;
                handshakeOk = sawHandshake = true;
                pendingSinceTick = null;
                rehandshakeSuspectTick = 0;
                rehandshakeGen++;
                rehandshakeConfirmedDown = false;
                rehandshakeOnlyChecked10s = false;
                activeProbeRunning = false;
                if (recovering && !suppressEvents)
                {
                    NetMon.Record("WireSock", NetMon.EventKind.Info, "Re-handshake WireGuard recuperado");
                    ThreadPool.QueueUserWorkItem(_ => ConfFile.FlushDnsCache());
                }
                return;
            }
            if (line.IndexOf("Handshake initiation sent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Sending handshake initiation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sawHandshake = true;
                // Only track initial pre-handshake pending window; post-handshake rekeys must not flap UI due to 4KB stdout buffering
                if (!handshakeOk && pendingSinceTick == null)
                    pendingSinceTick = NowTicks();
                return;
            }
            if (line.IndexOf("Handshake did not complete", StringComparison.OrdinalIgnoreCase) >= 0)
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
                    rehandshakeOnlyChecked10s = false;
                    if (!suppressEvents) NetMon.Record("WireSock", NetMon.EventKind.Warn, "Handshake WireGuard não completou — verificando túnel");
                }
                return;
            }
            if (suppressEvents) return;

            string level;
            string msg = ExtractLogMessage(line, out level);
            if (string.IsNullOrEmpty(msg)) return;
            string low = msg.ToLowerInvariant();

            if (low.Contains("failed to inject bypass packet") || low.Contains("failed to enqueue packet for deferred processing"))
                RecordWireSockLogEvent("bypass", NetMon.EventKind.Error, "Driver falhou ao reinjetar pacote na placa física (bypass/fila)");
            else if (low.Contains("packet size is above mtu"))
            {
                if (NowTicks() > Interlocked.Read(ref suppressMtuLogUntilTick))
                {
                    RecordWireSockLogEvent("mtu", NetMon.EventKind.Warn, "Pacote excedeu MTU configurado no túnel");
                    if (settings.AutoHeal && settings.OptimizeMtu && wasRunning == true && handshakeOk && !bgMtuReducedThisSession)
                    {
                        if (mtuLogWindowStartTick == 0 || ElapsedSeconds(mtuLogWindowStartTick) > 30.0)
                        {
                            mtuLogWindowStartTick = NowTicks();
                            mtuLogBurstCount = 1;
                        }
                        else
                        {
                            mtuLogBurstCount++;
                        }
                        if (mtuLogBurstCount >= 3)
                        {
                            mtuLogBurstCount = 0;
                            mtuLogWindowStartTick = 0;
                            QueueBackgroundAutoHeal(true, true);
                        }
                    }
                }
            }
            else if (low.Contains("ip_interface_changed_callback") || low.Contains("network adapter list changed"))
                RecordWireSockLogEvent("flap", NetMon.EventKind.Warn, "WireSock detectou alteração/flap de adaptador e reiniciará o filtro");
            else if (low.Contains("watchdog") || low.Contains("tunnel restart failed"))
                RecordWireSockLogEvent("watchdog", NetMon.EventKind.Error, "Watchdog do WireSock: " + (msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg));
            else if (level == "error" || level == "fatal" || level == "critical")
                RecordWireSockLogEvent("err", NetMon.EventKind.Error, msg.Length > 120 ? msg.Substring(0, 120) + "..." : msg);
            else if (level == "warning" || level == "warn")
                RecordWireSockLogEvent("warn", NetMon.EventKind.Warn, msg.Length > 120 ? msg.Substring(0, 120) + "..." : msg);
            else if (string.IsNullOrEmpty(level) && (low.Contains("error") || low.Contains("exception") || low.Contains("failed") || low.Contains("fatal")))
                RecordWireSockLogEvent("err", NetMon.EventKind.Error, msg.Length > 120 ? msg.Substring(0, 120) + "..." : msg);
        }

        void ReadHandshakes()
        {
            try
            {
                if (logStream == null)
                {
                    if (!File.Exists(Paths.Log)) return;
                    logStream = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                    logDecoder.Reset();
                }
                long len = logStream.Length;
                bool isTailReplay = false;
                if (seekToTail)
                {
                    seekToTail = false;
                    isTailReplay = true;
                    if (len > 65536) logPos = len - 65536;
                }
                if (len < logPos)
                {
                    // log was recreated/truncated
                    logPos = 0;
                    logRest = "";
                    logDecoder.Reset();
                }
                if (len == logPos) return;
                logStream.Seek(logPos, SeekOrigin.Begin);
                var sb = new StringBuilder(logRest);
                while (logPos < len)
                {
                    int toRead = (int)Math.Min(logBuf.Length, len - logPos);
                    int n = logStream.Read(logBuf, 0, toRead);
                    if (n <= 0) break;
                    logPos += n;
                    int chars = logDecoder.GetChars(logBuf, 0, n, logChars, 0, false);
                    sb.Append(logChars, 0, chars);
                }
                string text = sb.ToString();
                int cut = text.LastIndexOf('\n');
                logRest = cut >= 0 ? text.Substring(cut + 1) : text;
                if (cut >= 0)
                {
                    foreach (string line in text.Substring(0, cut).Split('\n'))
                        InspectHandshakeLine(line, isTailReplay);
                }
            }
            catch
            {
                CloseLogStream();
            }
        }
    }
}
