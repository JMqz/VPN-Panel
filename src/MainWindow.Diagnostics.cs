// Main window: Tests tab: quick/continuous diagnostics and repairs.

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
        void RecordNetworkAddressDelta(bool silent)
        {
            try
            {
                var parts = new List<string>();
                var physicalWithGwNames = new List<string>();
                bool hasPhysicalGw = false;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                        ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    string v4Ip = "";
                    string gwIp = "";
                    try
                    {
                        var props = ni.GetIPProperties();
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                v4Ip = ua.Address.ToString();
                                break;
                            }
                        }
                        foreach (var gw in props.GatewayAddresses)
                        {
                            if (gw.Address != null && gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                !IPAddress.Any.Equals(gw.Address))
                            {
                                gwIp = gw.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(v4Ip) && string.IsNullOrEmpty(gwIp)) continue;
                    string descLow = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
                    bool isVirtual = descLow.Contains("wiresock") || descLow.Contains("wintun") || descLow.Contains("wireguard") ||
                                     descLow.Contains("tailscale") || descLow.Contains("warp") || descLow.Contains("hyper-v") ||
                                     descLow.Contains("exitlag") || descLow.Contains("radmin") || descLow.Contains("hamachi") ||
                                     descLow.Contains("zerotier") || descLow.Contains("vmware") || descLow.Contains("virtualbox");
                    if (!isVirtual && !string.IsNullOrEmpty(gwIp))
                    {
                        hasPhysicalGw = true;
                        physicalWithGwNames.Add(ni.Name);
                    }
                    parts.Add(ni.Name + "=" + v4Ip + (string.IsNullOrEmpty(gwIp) ? "" : "/GW:" + gwIp));
                }
                parts.Sort(StringComparer.OrdinalIgnoreCase);
                string sig = string.Join(" | ", parts.ToArray());
                lock (netSigLock)
                {
                    if (sig == lastNetSignature) return;
                    lastNetSignature = sig;
                }
                if (silent) return;
                string summary = string.IsNullOrEmpty(sig) ? "Nenhuma interface IPv4 ativa" : sig;
                NetMon.Record("Rede", hasPhysicalGw ? NetMon.EventKind.Info : NetMon.EventKind.Warn,
                    "Mudança de interface/IP: " + summary);
                if (physicalWithGwNames.Count > 1 && (lastDualNicWarnTick == 0 || ElapsedSeconds(lastDualNicWarnTick) >= 120.0))
                {
                    lastDualNicWarnTick = NowTicks();
                    NetMon.Record("Rede", NetMon.EventKind.Warn,
                        "Múltiplas conexões físicas ativas (" + string.Join(" + ", physicalWithGwNames.ToArray()) +
                        ") — oscilações no Wi-Fi reiniciam o filtro do WireSock durante jogos");
                }
            }
            catch { }
        }

        void OnDiagPageShown()
        {
            UpdateDiagRouteBadge();
            UpdateDiagTimeline();
            if (!diagQuickRunning && string.IsNullOrEmpty(lastDiagSummary))
                RunQuickDiag();
        }

        void UpdateDiagTimeline()
        {
            if (diagTimelineList == null || txtDiagTimelineEmpty == null) return;
            long v = NetMon.Version;
            if (v == lastTimelineVersion) return;
            lastTimelineVersion = v;

            NetMon.EventItem[] items = NetMon.Snapshot(10);
            diagTimelineList.Children.Clear();
            if (items == null || items.Length == 0)
            {
                txtDiagTimelineEmpty.Visibility = Visibility.Visible;
                diagTimelineList.Visibility = Visibility.Collapsed;
                return;
            }
            txtDiagTimelineEmpty.Visibility = Visibility.Collapsed;
            diagTimelineList.Visibility = Visibility.Visible;

            for (int i = items.Length - 1; i >= 0; i--)
            {
                NetMon.EventItem it = items[i];
                var row = new Grid { Margin = new Thickness(0, 0, 0, i == 0 ? 0 : 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                string brushKey = it.Kind == NetMon.EventKind.Error ? "Danger"
                                : it.Kind == NetMon.EventKind.Warn ? "Warn"
                                : "SecureText";
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = B(brushKey),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 5, 8, 0)
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var txt = new TextBlock
                {
                    Text = string.Format("{0:HH:mm:ss} [{1}] {2}", it.Time, it.Category, it.Message),
                    FontSize = 11.5,
                    Foreground = it.Kind == NetMon.EventKind.Error ? B("Danger") : it.Kind == NetMon.EventKind.Warn ? B("Warn") : B("Muted"),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(txt, 1);
                row.Children.Add(txt);
                diagTimelineList.Children.Add(row);
            }
        }

        static NetDiag.ProbeResult ProbeIcmpSingle(IPAddress ip, int timeoutMs, CancellationToken ct)
        {
            if (ip == null) return new NetDiag.ProbeResult { Ok = false, LatencyMs = -1, Target = "", Detail = "Sem gateway" };
            if (ct.IsCancellationRequested) return new NetDiag.ProbeResult { Ok = false, LatencyMs = -1, Target = ip.ToString(), Detail = "Cancelado" };
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var rep = ping.Send(ip, Math.Max(300, timeoutMs));
                    if (rep != null && rep.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        return new NetDiag.ProbeResult
                        {
                            Ok = true,
                            LatencyMs = Math.Max(1, (int)rep.RoundtripTime),
                            Target = ip.ToString(),
                            Detail = "ICMP OK"
                        };
                    }
                    return new NetDiag.ProbeResult
                    {
                        Ok = false,
                        LatencyMs = -1,
                        Target = ip.ToString(),
                        Detail = rep != null ? rep.Status.ToString() : "Sem resposta"
                    };
                }
            }
            catch (Exception ex)
            {
                return new NetDiag.ProbeResult { Ok = false, LatencyMs = -1, Target = ip.ToString(), Detail = ex.Message };
            }
        }

        static void ScanRecentLogSignals(out string driverErr, out string logWarn, out bool hasBypassFail, out bool hasMtuErr, out bool hasAdapterFlap)
        {
            driverErr = null;
            logWarn = null;
            hasBypassFail = false;
            hasMtuErr = false;
            hasAdapterFlap = false;
            try
            {
                if (!File.Exists(Paths.Log)) return;
                using (var fs = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > 32768) fs.Seek(fs.Length - 32768, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs))
                    {
                        string[] lines = r.ReadToEnd().Split('\n');
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            string l = lines[i].Trim();
                            if (l.Length == 0) continue;
                            string lvl;
                            string msg = ExtractLogMessage(l, out lvl);
                            if (string.IsNullOrEmpty(msg)) continue;
                            string low = msg.ToLowerInvariant();
                            if (low.Contains("failed to inject bypass packet") || low.Contains("failed to enqueue packet for deferred processing"))
                            {
                                hasBypassFail = true;
                                if (driverErr == null) driverErr = "Falha de reinjeção/bypass no driver WireSock";
                            }
                            if (low.Contains("packet size is above mtu"))
                            {
                                hasMtuErr = true;
                                if (logWarn == null) logWarn = "Pacote acima do MTU no túnel";
                            }
                            if (low.Contains("ip_interface_changed_callback") || low.Contains("network adapter list changed") || low.Contains("watchdog"))
                            {
                                hasAdapterFlap = true;
                                if (logWarn == null) logWarn = "WireSock reiniciou filtro por alteração de adaptador";
                            }
                            if (driverErr == null && (lvl == "error" || lvl == "fatal" || lvl == "critical"))
                                driverErr = msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg;
                            if (logWarn == null && (lvl == "warning" || lvl == "warn"))
                                logWarn = msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg;
                        }
                    }
                }
            }
            catch { }
        }

        void UpdateDiagRouteBadge()
        {
            if (diagRouteBadge == null || txtDiagRouteBadge == null) return;
            AppMode eff = EffectiveAppMode();
            string srvName = active != null ? active.Name : "sem servidor";
            if (wasRunning == true)
            {
                if (eff == AppMode.Only)
                {
                    bool hasHelper = Tunnel.ProbeAppInjected;
                    txtDiagRouteBadge.Text = hasHelper
                        ? "Modo Só estes — Internet Base por fora · sonda UDP interna testa VPN (" + srvName + ")"
                        : "Modo Só estes — testes vão por fora · VPN (" + srvName + "): " + (handshakeOk ? "ativa" : "conectando");
                    diagRouteBadge.Background = hasHelper && handshakeOk ? B("AccentSoft") : B("WarnSoft");
                    txtDiagRouteBadge.Foreground = hasHelper && handshakeOk ? B("Accent") : B("Warn");
                }
                else if (eff == AppMode.Except)
                {
                    txtDiagRouteBadge.Text = "Rota do teste: Pela VPN (" + srvName + " · Exceto lista)";
                    diagRouteBadge.Background = handshakeOk ? B("SecureSoft") : B("WarnSoft");
                    txtDiagRouteBadge.Foreground = handshakeOk ? B("SecureText") : B("Warn");
                }
                else
                {
                    string engineTag = Tunnel.ActiveEngine == TunnelEngine.Wintun ? " · Wintun L3" : "";
                    txtDiagRouteBadge.Text = "Rota do teste: Pela VPN (" + srvName + " · Todos" + engineTag + ")";
                    diagRouteBadge.Background = handshakeOk ? B("SecureSoft") : B("WarnSoft");
                    txtDiagRouteBadge.Foreground = handshakeOk ? B("SecureText") : B("Warn");
                }
            }
            else
            {
                txtDiagRouteBadge.Text = "Rota do teste: Internet direta (VPN desconectada)";
                diagRouteBadge.Background = B("Surface2");
                txtDiagRouteBadge.Foreground = B("Muted");
            }
        }

        void CancelQuickDiag(bool disableBtn)
        {
            diagEpoch++;
            diagQuickRunning = false;
            if (diagQuickCts != null)
            {
                try { diagQuickCts.Cancel(); } catch { }
                diagQuickCts = null;
            }
            if (btnDiagQuick != null)
            {
                btnDiagQuick.IsEnabled = !disableBtn && !diagRepairRunning;
                btnDiagQuick.Content = "Testar agora";
            }
        }

        void CancelAllDiag()
        {
            CancelQuickDiag(false);
            diagContEpoch++;
            diagContinuousRunning = false;
            if (diagContCts != null)
            {
                try { diagContCts.Cancel(); } catch { }
                diagContCts = null;
            }
            if (btnDiagRepair != null)
            {
                btnDiagRepair.IsEnabled = true;
                btnDiagRepair.Content = "Corrigir problemas encontrados";
            }
            if (btnDiagContinuous != null) btnDiagContinuous.Content = "Teste contínuo";
        }

        static void SetDiagDot(Ellipse dot, string brushKey)
        {
            if (dot != null) dot.Fill = B(brushKey);
        }

        static NetDiag.AuditItem FindAudit(List<NetDiag.AuditItem> items, string key)
        {
            if (items == null) return null;
            foreach (var item in items)
                if (item.Key == key) return item;
            return null;
        }

        void RunQuickDiag() { RunQuickDiag(null); }

        void RunQuickDiag(string postFixNote)
        {
            if (exiting || pageDiag == null) return;
            UpdateDiagRouteBadge();

            if (diagQuickCts != null)
            {
                try { diagQuickCts.Cancel(); } catch { }
            }
            CancellationTokenSource myCts = new CancellationTokenSource();
            diagQuickCts = myCts;
            CancellationToken ct = myCts.Token;
            int myEpoch = ++diagEpoch;

            diagQuickRunning = true;
            btnDiagQuick.IsEnabled = false;
            btnDiagQuick.Content = "Testando...";
            btnDiagRepair.IsEnabled = true;
            btnDiagRepair.Content = "Corrigir problemas encontrados";
            btnDiagRepair.Visibility = Visibility.Collapsed;
            btnDiagApps.Visibility = active != null ? Visibility.Visible : Visibility.Collapsed;

            SetDiagDot(diagVerdictDot, "Muted");
            txtDiagVerdict.Foreground = B("Text");
            txtDiagVerdict.Text = "Analisando conexão...";
            txtDiagVerdictSub.Text = "Testando internet, UDP, DNS, gateway, MTU, conflitos e logs...";
            SetDiagDot(chkItem1Dot, "Muted"); txtChk1Detail.Text = "Testando acesso HTTPS, UDP e gateway...";
            SetDiagDot(chkItem2Dot, "Muted"); txtChk2Detail.Text = "Testando resolução de nomes...";
            SetDiagDot(chkItem3Dot, "Muted"); txtChk3Detail.Text = "Verificando servidor, túnel e log...";
            SetDiagDot(chkItem4Dot, "Muted"); txtChk4Detail.Text = "Verificando kill switch e IPv6...";
            SetDiagDot(chkItem5Dot, "Muted"); txtChk5Detail.Text = "Auditando configuração e apps...";
            SetDiagDot(chkItem6Dot, "Muted"); txtChk6Detail.Text = "Testando fragmentação MTU (1472/1392/1352)...";
            SetDiagDot(chkItem7Dot, "Muted"); txtChk7Detail.Text = "Verificando serviços, drivers e adaptadores...";

            ConfigInfo cfgSnap = active;
            var sSnap = new Settings
            {
                NetworkLock = settings.NetworkLock,
                OptimizeMtu = settings.OptimizeMtu,
                AdaptiveMtu = settings.AdaptiveMtu,
                AutoHeal = settings.AutoHeal,
                SharedApps = settings.SharedApps,
                AppsMode = settings.AppsMode,
                Apps = new List<string>(settings.Apps)
            };
            bool tunnelRunning = wasRunning == true;
            bool hsOk = handshakeOk;
            bool v6Isolated = Tunnel.Ipv6Isolated;
            bool wsInstalledSnap = wireSockInstalled;
            bool appsPending = appsNeedReconnect;
            AppMode effMode = EffectiveAppMode();
            int effMtuSnap = DiagEffectiveMtu(tunnelRunning, effMode, sSnap);
            TimeSpan up = (tunnelRunning && hsOk) ? (DateTime.Now - startedAt) : TimeSpan.Zero;
            string uptimeStr = (tunnelRunning && hsOk)
                ? string.Format("{0:00}:{1:00}:{2:00}", (int)up.TotalHours, up.Minutes, up.Seconds)
                : "";
            string logHint = (!hsOk || !tunnelRunning) ? LogTail() : "";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool uiPosted = false;
                try
                {
                    if (ct.IsCancellationRequested) return;
                    string driverLogErr, driverLogWarn;
                    bool logBypassFail, logMtuErr, logAdapterFlap;
                    ScanRecentLogSignals(out driverLogErr, out driverLogWarn, out logBypassFail, out logMtuErr, out logAdapterFlap);

                    bool hasGhostV6 = ConfFile.HasGhostIpv6();
                    bool pendingLock = Tunnel.HasPendingNetworkLock;
                    List<NetDiag.AuditItem> audit = NetDiag.AuditConfig(cfgSnap, sSnap, hasGhostV6, tunnelRunning, appsPending);
                    NetDiag.ProbeResult httpRes = NetDiag.ProbeHttp204(3000, ct);
                    NetDiag.ProbeResult dnsRes = NetDiag.ProbeDns("cloudflare.com", ct);
                    NetDiag.ProbeResult udpRes = NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, ct);
                    IPAddress gwIp = NetDiag.GetPhysicalGateway();
                    NetDiag.ProbeResult gwRes = gwIp != null ? ProbeIcmpSingle(gwIp, 1200, ct) : null;
                    NetDiag.ProbeResult epRes = cfgSnap != null ? NetDiag.ProbeEndpoint(cfgSnap, 2000, ct) : null;
                    NetDiag.ProbeResult tunOnlyRes = (tunnelRunning && effMode == AppMode.Only && Tunnel.ProbeAppInjected)
                        ? NetDiag.ProbeTunnelUdpViaHelper("1.1.1.1", 53, 1500, ct)
                        : null;
                    NetDiag.MtuResult mtuRes;
                    try
                    {
                        Interlocked.Exchange(ref suppressMtuLogUntilTick, FutureTicks(25.0));
                        mtuRes = NetDiag.ProbeMtu(IPAddress.Parse("1.1.1.1"), ct);
                        if (!mtuRes.Ok && !ct.IsCancellationRequested &&
                            mtuRes.Detail != null && mtuRes.Detail.IndexOf("ICMP bloqueado", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mtuRes = NetDiag.ProbeMtu(IPAddress.Parse("8.8.8.8"), ct);
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref suppressMtuLogUntilTick, FutureTicks(1.5));
                    }
                    IList<string> effAppsSnap = sSnap.SharedApps ? (IList<string>)sSnap.Apps : (cfgSnap != null ? (IList<string>)cfgSnap.Apps : null);
                    NetDiag.ConflictReport conflicts = NetDiag.DetectConflicts(effMode, effAppsSnap);
                    int tunnelMtuCap = sSnap.OptimizeMtu ? (sSnap.AdaptiveMtu > 0 ? sSnap.AdaptiveMtu : 1380) : 0;
                    int cfgMtu = cfgSnap != null && cfgSnap.Mtu > 0 ? cfgSnap.Mtu : 0;
                    int actualTunnelMtu = tunnelMtuCap > 0
                        ? (cfgMtu > 0 && cfgMtu < tunnelMtuCap ? cfgMtu : tunnelMtuCap)
                        : (cfgMtu > 0 ? cfgMtu : 1420);
                    IPAddress epIpAddr;
                    bool ipv6Endpoint = (cfgSnap != null && !string.IsNullOrEmpty(cfgSnap.Endpoint) && cfgSnap.Endpoint.Contains(":")) ||
                                        (epRes != null && !string.IsNullOrEmpty(epRes.Target) && IPAddress.TryParse(epRes.Target, out epIpAddr) && epIpAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                    int wgOverhead = ipv6Endpoint ? 80 : 60;
                    bool mtuPhysicalNormal = (!tunnelRunning || effMode != AppMode.All) && !logMtuErr &&
                                             mtuRes.Ok && mtuRes.MaxPayload >= 1392 && mtuRes.MaxPayload < 1472 &&
                                             sSnap.OptimizeMtu && tunnelMtuCap > 0 && (actualTunnelMtu + wgOverhead <= mtuRes.MaxPayload + 28);
                    int suggestedMtu = AutoHealer.SuggestSafeMtu(mtuRes, logMtuErr, effMtuSnap);
                    bool canFixMtu = suggestedMtu > 0 && !mtuPhysicalNormal && (!sSnap.OptimizeMtu || effMtuSnap == 0 || suggestedMtu < effMtuSnap);

                    // Recent NetMon events (last 5 minutes)
                    DateTime cutoff = DateTime.Now.AddMinutes(-5);
                    NetMon.EventItem[] recentEvents = NetMon.Snapshot(40);
                    int recentNetFlaps = 0, recentDriverWarns = 0;
                    for (int i = 0; i < recentEvents.Length; i++)
                    {
                        if (recentEvents[i].Time < cutoff) continue;
                        if (recentEvents[i].Category == "Rede" && recentEvents[i].Kind != NetMon.EventKind.Info) recentNetFlaps++;
                        if (recentEvents[i].Category == "WireSock" && recentEvents[i].Kind != NetMon.EventKind.Info) recentDriverWarns++;
                    }

                    bool cancelled = ct.IsCancellationRequested;
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui == null) return;
                    uiPosted = true;
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (exiting || myEpoch != diagEpoch) return;
                        diagQuickRunning = false;
                        btnDiagQuick.IsEnabled = !diagRepairRunning;
                        btnDiagQuick.Content = "Testar agora";
                        if (cancelled || diagRepairRunning) return;
                        UpdateDiagRouteBadge();

                        // 1. Internet Base (HTTP 204 + UDP DNS + Gateway)
                        string gwTag = gwRes == null ? "" : (gwRes.Ok ? " · GW " + gwRes.LatencyMs + " ms" : " · GW falhou (" + gwRes.Detail + ")");
                        string udpTag = udpRes.Ok ? " · UDP " + udpRes.LatencyMs + " ms" : " · UDP falhou (" + udpRes.Detail + ")";
                        string d1;
                        if (httpRes.Ok && udpRes.Ok)
                        {
                            SetDiagDot(chkItem1Dot, "SecureText");
                            d1 = "OK em " + httpRes.LatencyMs + " ms" + udpTag + gwTag +
                                 (tunnelRunning && effMode == AppMode.Only ? " — via bypass" : "");
                        }
                        else if (httpRes.Ok && !udpRes.Ok)
                        {
                            SetDiagDot(chkItem1Dot, "Warn");
                            d1 = "HTTP OK (" + httpRes.LatencyMs + " ms), mas UDP:53 falhou (" + udpRes.Detail + ")" + gwTag;
                        }
                        else if (!tunnelRunning && pendingLock)
                        {
                            SetDiagDot(chkItem1Dot, "Danger");
                            d1 = "Sem acesso (" + httpRes.Detail + ") — bloqueio de rede pendente detectado.";
                        }
                        else
                        {
                            SetDiagDot(chkItem1Dot, "Danger");
                            d1 = "Falhou (" + httpRes.Detail + ")" + udpTag + gwTag;
                        }
                        txtChk1Detail.Text = d1;

                        // 2. DNS do Windows
                        string d2;
                        if (dnsRes.Ok)
                        {
                            SetDiagDot(chkItem2Dot, "SecureText");
                            d2 = "Resolvido em " + dnsRes.LatencyMs + " ms (" + dnsRes.Target + ")";
                        }
                        else
                        {
                            SetDiagDot(chkItem2Dot, "Danger");
                            d2 = "Falhou (" + dnsRes.Detail + ")" + (udpRes.Ok ? " — DNS UDP bruto respondeu, possível trava no Dnscache." : "");
                        }
                        txtChk2Detail.Text = d2;

                        // 3. Túnel & Servidor VPN
                        string d3;
                        string logExtra = !string.IsNullOrEmpty(driverLogErr) ? (" · Log: " + driverLogErr)
                                        : !string.IsNullOrEmpty(driverLogWarn) ? (" · Log: " + driverLogWarn) : "";
                        if (cfgSnap == null)
                        {
                            SetDiagDot(chkItem3Dot, "Warn");
                            d3 = "Nenhum servidor selecionado.";
                        }
                        else if (tunnelRunning && hsOk)
                        {
                            string tunTag = tunOnlyRes == null ? ""
                                          : (tunOnlyRes.Ok ? " · túnel Só estes UDP " + tunOnlyRes.LatencyMs + " ms" : " · túnel Só estes UDP falhou (" + tunOnlyRes.Detail + ")");
                            if (tunOnlyRes != null && !tunOnlyRes.Ok)
                            {
                                SetDiagDot(chkItem3Dot, "Danger");
                                d3 = "Conectado há " + uptimeStr + tunTag + logExtra;
                            }
                            else if (epRes != null && epRes.Ok)
                            {
                                SetDiagDot(chkItem3Dot, !string.IsNullOrEmpty(driverLogErr) ? "Warn" : "SecureText");
                                d3 = "Conectado há " + uptimeStr + " · endpoint " + epRes.LatencyMs + " ms" + tunTag + logExtra;
                            }
                            else
                            {
                                SetDiagDot(chkItem3Dot, tunOnlyRes != null && tunOnlyRes.Ok ? "SecureText" : "Warn");
                                d3 = "Túnel ativo há " + uptimeStr + tunTag + " (endpoint sem resposta ICMP/TCP: " +
                                     (epRes != null ? epRes.Detail : "sem resposta") + ")" + logExtra;
                            }
                        }
                        else if (tunnelRunning)
                        {
                            SetDiagDot(chkItem3Dot, "Warn");
                            d3 = "WireSock aberto, aguardando handshake · endpoint: " +
                                 (epRes != null && epRes.Ok ? epRes.LatencyMs + " ms" : (epRes != null ? epRes.Detail : "sem resposta")) + logExtra;
                        }
                        else
                        {
                            if (epRes != null && epRes.Ok)
                            {
                                SetDiagDot(chkItem3Dot, "SecureText");
                                d3 = "Desconectado · endpoint acessível em " + epRes.LatencyMs + " ms (" + epRes.Target + ")";
                            }
                            else
                            {
                                SetDiagDot(chkItem3Dot, "Danger");
                                d3 = "Desconectado · endpoint inacessível (" + (epRes != null ? epRes.Detail : "erro") + ")";
                            }
                        }
                        txtChk3Detail.Text = d3;

                        // 4. Kill Switch & IPv6
                        bool ghostAlert = hasGhostV6 && (!tunnelRunning || v6Isolated);
                        NetDiag.AuditItem lockFlagItem = FindAudit(audit, "lock_flag");
                        NetDiag.AuditItem ghostV6Item = FindAudit(audit, "ghost_v6");
                        NetDiag.AuditItem lockSplitItem = FindAudit(audit, "lock_split");
                        bool item4HasErr = false;
                        bool item4HasWarn = false;
                        string d4;
                        if (lockFlagItem != null || (!tunnelRunning && pendingLock))
                        {
                            item4HasErr = true;
                            SetDiagDot(chkItem4Dot, "Danger");
                            d4 = lockFlagItem != null ? lockFlagItem.Detail : "Bloqueio residual do Kill Switch (WFP) ativo sem VPN.";
                        }
                        else if (ghostAlert && ghostV6Item != null && ghostV6Item.Severity != NetDiag.AuditSeverity.Ok)
                        {
                            item4HasWarn = true;
                            SetDiagDot(chkItem4Dot, "Warn");
                            d4 = ghostV6Item.Detail;
                        }
                        else if (lockSplitItem != null && lockSplitItem.Severity != NetDiag.AuditSeverity.Ok)
                        {
                            item4HasWarn = true;
                            SetDiagDot(chkItem4Dot, "Warn");
                            d4 = lockSplitItem.Detail;
                        }
                        else
                        {
                            SetDiagDot(chkItem4Dot, "SecureText");
                            d4 = (tunnelRunning && sSnap.NetworkLock && effMode == AppMode.All)
                                ? "Kill switch ativo com VPN conectada; sem rotas IPv6 fantasmas."
                                : "Sem bloqueio residual (WFP) e sem rota IPv6 fantasma.";
                        }
                        txtChk4Detail.Text = d4;

                        // 5. Configuração & Apps
                        var item5Problems = new List<string>();
                        bool item5HasErr = false;
                        bool item5HasWarn = false;
                        foreach (string k in new[] { "config", "keepalive", "mtu", "lan", "apps_pending" })
                        {
                            NetDiag.AuditItem it = FindAudit(audit, k);
                            if (it != null && it.Severity != NetDiag.AuditSeverity.Ok)
                            {
                                if (it.Severity == NetDiag.AuditSeverity.Error) item5HasErr = true;
                                else item5HasWarn = true;
                                item5Problems.Add(it.Detail);
                            }
                        }
                        int appCount = settings.SharedApps ? sSnap.Apps.Count : (cfgSnap != null ? cfgSnap.Apps.Count : 0);
                        string modeLabel = effMode == AppMode.All ? "Todos os apps"
                                         : effMode == AppMode.Only ? "Só estes (" + appCount + ")"
                                         : "Exceto (" + appCount + ")";
                        string d5;
                        if (item5Problems.Count > 0)
                        {
                            SetDiagDot(chkItem5Dot, item5HasErr ? "Danger" : "Warn");
                            d5 = modeLabel + ". " + string.Join(" ", item5Problems.ToArray());
                        }
                        else
                        {
                            SetDiagDot(chkItem5Dot, "SecureText");
                            d5 = "Configuração saudável (" + modeLabel + ", keepalive e MTU OK).";
                        }
                        txtChk5Detail.Text = d5;

                        // 6. MTU & Pacotes de Jogos
                        bool mtuIntentionalCap = sSnap.OptimizeMtu && effMtuSnap > 0 && !logMtuErr &&
                                                 mtuRes.Ok && mtuRes.MaxPayload >= effMtuSnap - 28;
                        bool mtuBlockedOnly = !mtuRes.Ok && mtuRes.Detail != null && mtuRes.Detail.IndexOf("ICMP bloqueado", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool mtuFrag = !mtuIntentionalCap && !mtuPhysicalNormal && ((!mtuRes.Ok && !mtuBlockedOnly) || (mtuRes.Ok && mtuRes.MaxPayload < 1472) || logMtuErr);
                        string d6;
                        if (mtuIntentionalCap)
                        {
                            d6 = (sSnap.AdaptiveMtu > 0 ? "MTU adaptativo (" + effMtuSnap + " B)" : "MTU otimizado (" + effMtuSnap + " B)") +
                                 " ativo e sem fragmentação (" + mtuRes.Detail + ").";
                            SetDiagDot(chkItem6Dot, udpRes.Ok ? "SecureText" : "Warn");
                        }
                        else if (mtuPhysicalNormal)
                        {
                            d6 = "Rede física em MTU " + (mtuRes.MaxPayload + 28) + " (" + mtuRes.MaxPayload + " B · túnel ajustado em " + actualTunnelMtu + " B sem fragmentação).";
                            SetDiagDot(chkItem6Dot, udpRes.Ok ? "SecureText" : "Warn");
                        }
                        else
                        {
                            d6 = mtuRes.Detail;
                            if (logMtuErr)
                                d6 += " · Log do WireSock acusou pacote acima do MTU.";
                            if (mtuRes.Ok && mtuRes.MaxPayload == 1472 && !logMtuErr)
                                SetDiagDot(chkItem6Dot, udpRes.Ok ? "SecureText" : "Warn");
                            else if (mtuFrag)
                                SetDiagDot(chkItem6Dot, (!mtuRes.Ok || logMtuErr) ? "Danger" : "Warn");
                            else
                                SetDiagDot(chkItem6Dot, "Muted");
                        }
                        txtChk6Detail.Text = d6;

                        // 7. Conflitos & Driver
                        bool hardConflict = conflicts.WireSockServiceRunning || conflicts.WireSockProcessCount > 1 ||
                                            conflicts.ConflictingAdapters.Count > 0 || conflicts.ConflictingServices.Count > 0;
                        bool driverMissing = wsInstalledSnap && !conflicts.NdisDriverPresent && !conflicts.WintunActive;
                        bool hasPassiveTools = conflicts.PassiveNotes != null && conflicts.PassiveNotes.Count > 0;
                        bool passiveToolDuringFault = hasPassiveTools && (!udpRes.Ok || logBypassFail || recentDriverWarns > 0);
                        bool anyConflictWarn = hardConflict || driverMissing || conflicts.Warnings.Count > 0 || conflicts.LowMetricAdapters.Count > 0 || logBypassFail || logAdapterFlap || passiveToolDuringFault;
                        string engineLabel = conflicts.WintunActive
                            ? "Motor WireGuard/Wintun (L3 dedicado, sem filtro NDIS)"
                            : "Driver ndiswgc ativo";
                        string d7;
                        if (conflicts.Warnings.Count > 0)
                        {
                            SetDiagDot(chkItem7Dot, (hardConflict || driverMissing) ? "Danger" : "Warn");
                            d7 = string.Join(" ", conflicts.Warnings.ToArray());
                            if (hasPassiveTools) d7 += " " + string.Join(" ", conflicts.PassiveNotes.ToArray());
                        }
                        else if (logBypassFail || logAdapterFlap)
                        {
                            SetDiagDot(chkItem7Dot, logBypassFail ? "Danger" : "Warn");
                            d7 = logBypassFail ? "Sem conflitos externos, mas o driver falhou ao reinjetar pacotes (bypass)."
                                               : "Sem conflitos externos, mas houve oscilação/reinício de adaptador no log.";
                            if (hasPassiveTools) d7 += " " + string.Join(" ", conflicts.PassiveNotes.ToArray());
                        }
                        else if (hasPassiveTools)
                        {
                            SetDiagDot(chkItem7Dot, passiveToolDuringFault ? "Warn" : "SecureText");
                            d7 = (passiveToolDuringFault
                                ? engineLabel + ", mas houve falha durante uso com programas em espera: "
                                : engineLabel + " · ") + string.Join(" ", conflicts.PassiveNotes.ToArray());
                        }
                        else
                        {
                            SetDiagDot(chkItem7Dot, "SecureText");
                            d7 = engineLabel + " · sem VPNs, serviços ou rotas concorrentes.";
                        }
                        txtChk7Detail.Text = d7;

                        int fixableCount = 0;
                        if (!tunnelRunning && pendingLock) fixableCount++;
                        if (ghostAlert) fixableCount++;
                        if (!dnsRes.Ok) fixableCount++;
                        if (conflicts.WireSockServiceRunning) fixableCount++;
                        if ((!tunnelRunning && conflicts.WireSockProcessCount > 0) || (tunnelRunning && conflicts.WireSockProcessCount > 1)) fixableCount++;
                        if (conflicts.ConflictingServices.Count > 0) fixableCount += conflicts.ConflictingServices.Count;
                        if (conflicts.ExitLagGameOverlap && conflicts.OverlappingGames.Count > 0) fixableCount++;
                        if (canFixMtu) fixableCount++;

                        lastDiagMtu = mtuRes;
                        lastDiagConflicts = conflicts;
                        lastDiagDnsFailed = !dnsRes.Ok;
                        lastDiagLogMtuErr = logMtuErr;
                        lastDiagFixableCount = fixableCount;

                        bool showRepair = fixableCount > 0 || (!httpRes.Ok && !dnsRes.Ok && !tunnelRunning);
                        btnDiagRepair.Content = fixableCount > 0 ? ("Corrigir problemas encontrados (" + fixableCount + ")") : "Reparar internet agora";
                        btnDiagRepair.Visibility = showRepair ? Visibility.Visible : Visibility.Collapsed;

                        // Motor de Veredito Causa-Raiz (Tabela de prioridade em PT-BR claro)
                        string vColor = "SecureText";
                        string vTitle, vSub;
                        bool gwBlockedByTunnel = (epRes != null && epRes.Ok) || httpRes.Ok || udpRes.Ok || (tunnelRunning && effMode == AppMode.All);
                        bool physicalGwDown = gwRes != null && !gwRes.Ok && !gwBlockedByTunnel;
                        bool allNetDown = !httpRes.Ok && !dnsRes.Ok && !udpRes.Ok;
                        bool endpointDown = epRes != null && !epRes.Ok;

                        if (!tunnelRunning && pendingLock)
                        {
                            vColor = "Danger";
                            vTitle = "Internet bloqueada pelo Kill Switch";
                            vSub = "Um bloqueio residual (WFP) permaneceu ativo sem a VPN. Clique em \"Reparar internet agora\".";
                        }
                        else if (hardConflict)
                        {
                            vColor = "Danger";
                            vTitle = "Conflito com outro serviço VPN / driver";
                            vSub = conflicts.Warnings.Count > 0 ? conflicts.Warnings[0] : "Feche a outra VPN ou serviço concorrente para estabilizar jogos e apps.";
                        }
                        else if (physicalGwDown || (allNetDown && (endpointDown || !tunnelRunning || effMode == AppMode.Only)))
                        {
                            vColor = "Danger";
                            vTitle = "A falha foi na sua internet / Wi-Fi";
                            vSub = physicalGwDown
                                ? "Seu roteador/gateway físico (" + gwRes.Target + ") não respondeu. Verifique Wi-Fi, cabo ou modem."
                                : ghostAlert
                                    ? "Sem resposta externa e há rota IPv6 fantasma. Verifique o Wi-Fi ou clique em \"Reparar internet agora\"."
                                    : "Sem resposta de DNS, UDP nem HTTPS na conexão física. Verifique seu Wi-Fi ou operadora.";
                        }
                        else if (driverMissing || logBypassFail || (tunnelRunning && effMode == AppMode.Only && (!udpRes.Ok || !httpRes.Ok)))
                        {
                            vColor = "Danger";
                            vTitle = "A falha foi no WireSock / driver derrubando a placa";
                            vSub = driverMissing
                                ? "O driver ndiswgc do WireSock não está ativo. Reinstale o WireSock e reinicie o PC."
                                : "O filtro do WireSock na placa física teve falha de bypass/reinício (" + (driverLogErr ?? udpRes.Detail) + ").";
                        }
                        else if ((!mtuIntentionalCap && !mtuPhysicalNormal && ((!mtuRes.Ok && !mtuBlockedOnly) || logMtuErr)) || (httpRes.Ok && !udpRes.Ok))
                        {
                            vColor = "Warn";
                            vTitle = "Fragmentação de MTU / perda UDP para jogos";
                            vSub = !udpRes.Ok
                                ? "Navegação HTTP passou, mas pacotes UDP (jogos como Roblox) falharam. Rode o Teste contínuo."
                                : "Pacotes grandes estão fragmentando (" + mtuRes.Detail + "). Ative 'Otimizar MTU' nas Opções.";
                        }
                        else if (tunnelRunning && !hsOk)
                        {
                            vColor = "Warn";
                            vTitle = "Servidor VPN instável ou sem resposta";
                            vSub = "O WireSock abriu, mas o servidor não respondeu ao handshake." +
                                   (string.IsNullOrEmpty(logHint) ? "" : " " + logHint);
                        }
                        else if (allNetDown && tunnelRunning && !endpointDown)
                        {
                            vColor = "Danger";
                            vTitle = "Servidor VPN instável ou sem resposta";
                            vSub = "Sua internet física alcança o servidor, mas o tráfego dentro do túnel não responde. Troque de servidor.";
                        }
                        else if (tunOnlyRes != null && !tunOnlyRes.Ok && tunnelRunning && !endpointDown)
                        {
                            vColor = "Danger";
                            vTitle = "Túnel Só estes sem resposta";
                            vSub = "Sua internet física está OK, mas o tráfego UDP dentro do túnel Só estes falhou (" + tunOnlyRes.Detail + "). Reconecte ou troque de servidor.";
                        }
                        else if (!dnsRes.Ok)
                        {
                            vColor = "Danger";
                            vTitle = "Falha na resolução DNS do Windows";
                            vSub = "A rede responde, mas nomes não resolvem (" + dnsRes.Detail + ").";
                        }
                        else if (!httpRes.Ok)
                        {
                            vColor = "Warn";
                            vTitle = "Internet instável ou bloqueada";
                            vSub = "DNS respondeu, mas HTTPS falhou (" + httpRes.Detail + ").";
                        }
                        else if (ghostAlert)
                        {
                            vColor = "Warn";
                            vTitle = "Internet OK, mas há rota IPv6 fantasma";
                            vSub = "Uma rota ::/0 residual pode causar travamentos. Clique em \"Reparar internet agora\".";
                        }
                        else if (anyConflictWarn || item4HasErr || item4HasWarn || item5HasErr || item5HasWarn || (!mtuIntentionalCap && !mtuPhysicalNormal && mtuRes.Ok && mtuRes.MaxPayload < 1472))
                        {
                            vColor = "Warn";
                            vTitle = tunnelRunning ? "Conexão ativa com avisos" : "Rede OK com avisos";
                            vSub = conflicts.Warnings.Count > 0 ? conflicts.Warnings[0]
                                 : passiveToolDuringFault ? string.Join(" ", conflicts.PassiveNotes.ToArray())
                                 : (!mtuPhysicalNormal && mtuRes.Ok && mtuRes.MaxPayload < 1472) ? mtuRes.Detail
                                 : item5Problems.Count > 0 ? item5Problems[0]
                                 : d4;
                        }
                        else
                        {
                            vColor = "SecureText";
                            vTitle = "Tudo funcionando normalmente";
                            vSub = (recentNetFlaps > 0 || recentDriverWarns > 0)
                                ? "Sem falhas agora, mas houve oscilações nos últimos 5 min (veja Linha do Tempo ou rode o Teste contínuo)."
                                : tunnelRunning
                                    ? (hasPassiveTools
                                        ? "Internet, UDP, DNS, driver e túnel aprovados (serviços em espera compatíveis sem Exit Node/uso simultâneo)."
                                        : "Internet, UDP, DNS, MTU, driver e servidor VPN aprovados.")
                                    : (hasPassiveTools
                                        ? "Internet, UDP, DNS, MTU e driver OK (VPN desconectada; serviços em espera compatíveis)."
                                        : "Internet, UDP, DNS, MTU e servidor acessíveis (VPN desconectada).");
                        }

                        if (!string.IsNullOrEmpty(postFixNote))
                        {
                            if (vColor == "SecureText") vTitle = "Corrigido e verificado";
                            vSub = "Correções aplicadas: " + postFixNote + " — Verificação atual: " + vSub;
                        }

                        SetDiagDot(diagVerdictDot, vColor);
                        txtDiagVerdict.Foreground = vColor == "Danger" ? B("Danger") : vColor == "Warn" ? B("Warn") : B("Text");
                        txtDiagVerdict.Text = vTitle;
                        txtDiagVerdictSub.Text = vSub;

                        var sb = new StringBuilder();
                        sb.AppendLine("VPN Panel v" + Program.Version + " — Diagnóstico (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")");
                        sb.AppendLine("Estado: " + (tunnelRunning ? (hsOk ? "Conectado" : "Conectando") : "Desconectado") +
                                      " | Servidor: " + (cfgSnap != null ? cfgSnap.Name : "nenhum") +
                                      " | Modo: " + effMode);
                        sb.AppendLine("Veredito: " + vTitle + " — " + vSub);
                        sb.AppendLine("1. Internet Base: " + d1);
                        sb.AppendLine("2. DNS do Windows: " + d2);
                        sb.AppendLine("3. Túnel & Servidor VPN: " + d3);
                        sb.AppendLine("4. Kill Switch & IPv6: " + d4);
                        sb.AppendLine("5. Configuração & Apps: " + d5);
                        sb.AppendLine("6. MTU & Pacotes de Jogos: " + d6);
                        sb.AppendLine("7. Conflitos & Driver: " + d7);
                        lastDiagSummary = sb.ToString().TrimEnd();
                        UpdateDiagTimeline();
                    }));
                }
                finally
                {
                    if (!uiPosted && !exiting)
                    {
                        var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                        if (ui != null)
                        {
                            ui.BeginInvoke(new Action(() =>
                            {
                                if (exiting || myEpoch != diagEpoch) return;
                                diagQuickRunning = false;
                                if (btnDiagQuick != null)
                                {
                                    btnDiagQuick.IsEnabled = !diagRepairRunning;
                                    btnDiagQuick.Content = "Testar agora";
                                }
                            }));
                        }
                    }
                    try { myCts.Dispose(); } catch { }
                }
            });
        }

        void ToggleContinuousDiag()
        {
            if (diagContinuousRunning)
            {
                StopContinuousDiag("Teste contínuo parado pelo usuário");
                return;
            }
            if (exiting || pageDiag == null) return;

            if (diagContCts != null)
            {
                try { diagContCts.Cancel(); } catch { }
            }
            CancellationTokenSource myCts = new CancellationTokenSource();
            diagContCts = myCts;
            CancellationToken ct = myCts.Token;
            int myContEpoch = ++diagContEpoch;

            diagContinuousRunning = true;
            lastContSummary = "";
            btnDiagContinuous.Content = "Parar teste";
            txtDiagLiveStatus.Text = "Executando rajada UDP (0/60s)";
            txtDiagLiveTime.Text = "00:00";
            txtDiagSentLoss.Text = "0 / 0%";
            txtDiagPing.Text = "— / —";
            txtDiagJitter.Text = "—";
            txtDiagDrops.Text = "0 / 0";
            txtDiagGw.Text = "—";
            txtDiagEp.Text = "—";

            ConfigInfo cfgSnap = active;
            AppMode effMode = EffectiveAppMode();
            bool tunnelRunning = wasRunning == true;

            diagContThread = new Thread(() =>
            {
                try
                {
                    int udpSent = 0, udpLost = 0;
                    int microDrops = 0, drops = 0, spikes200 = 0;
                    int consecDownSecs = 0;
                    int gwSent = 0, gwLost = 0;
                    long gwLatSum = 0;
                    int gwOkCount = 0;
                    bool gwFiltered = false;
                    int epOkCount = 0;
                    bool epFiltered = false;
                    int epLastMs = -1;
                    bool epLastOk = false;
                    double rfcJitter = 0;
                    int prevRtt = -1;
                    var latencies = new List<int>(300);
                    long lastEventLogTick = 0;

                    IPAddress gwIp = NetDiag.GetPhysicalGateway();
                    IPAddress epIp = null;
                    if (cfgSnap != null && !string.IsNullOrEmpty(cfgSnap.Endpoint))
                    {
                        if (!IPAddress.TryParse(cfgSnap.Endpoint, out epIp))
                        {
                            try
                            {
                                IPAddress[] addrs = Dns.GetHostAddresses(cfgSnap.Endpoint);
                                if (addrs != null && addrs.Length > 0) epIp = addrs[0];
                            }
                            catch { }
                        }
                    }

                    bool useTunnelSession = tunnelRunning && effMode == AppMode.Only && Tunnel.ProbeAppInjected;
                    using (var tunSession = useTunnelSession ? new NetDiag.TunnelUdpSession() : null)
                    using (var ping = new System.Net.NetworkInformation.Ping())
                    {
                        bool warnedHelperFallback = false;
                        Stopwatch totalSw = Stopwatch.StartNew();
                        int iter = 0;
                        while (!ct.IsCancellationRequested && totalSw.ElapsedMilliseconds < 60000)
                        {
                            iter++;
                            Stopwatch secSw = Stopwatch.StartNew();

                            if (gwIp == null && iter % 10 == 0) gwIp = NetDiag.GetPhysicalGateway();

                            // 5x UDP DNS probes (~200ms cadence) -> catches sub-second game microdrops
                            int burstOk = 0, burstLost = 0;
                            bool burstSpike250 = false;
                            bool usingTunThisSec = tunSession != null && tunSession.IsReady;
                            if (useTunnelSession && !usingTunThisSec && !warnedHelperFallback)
                            {
                                warnedHelperFallback = true;
                                NetMon.Record("Teste", NetMon.EventKind.Warn, "Sonda interna encerrou durante o teste; medições restantes seguem fora do túnel");
                            }
                            for (int b = 0; b < 5; b++)
                            {
                                if (ct.IsCancellationRequested || totalSw.ElapsedMilliseconds >= 60000) break;
                                bool useTun = tunSession != null && tunSession.IsReady;
                                NetDiag.ProbeResult u = useTun
                                    ? tunSession.Query("1.1.1.1", 53, 350, ct)
                                    : NetDiag.ProbeUdpDns("1.1.1.1", 53, 350, ct);
                                if (ct.IsCancellationRequested) break;
                                udpSent++;
                                if (u.Ok)
                                {
                                    burstOk++;
                                    latencies.Add(u.LatencyMs);
                                    if (u.LatencyMs > 200) spikes200++;
                                    if (u.LatencyMs > 250) burstSpike250 = true;
                                    if (prevRtt >= 0)
                                    {
                                        int diff = Math.Abs(u.LatencyMs - prevRtt);
                                        rfcJitter += (diff - rfcJitter) / 16.0;
                                    }
                                    prevRtt = u.LatencyMs;
                                }
                                else
                                {
                                    burstLost++;
                                    udpLost++;
                                }

                                if (b < 4)
                                {
                                    int nextSlotMs = (b + 1) * 200 - (int)secSw.ElapsedMilliseconds;
                                    int remMs = 60000 - (int)totalSw.ElapsedMilliseconds;
                                    int slotWaitMs = Math.Min(nextSlotMs, remMs);
                                    if (slotWaitMs > 0)
                                    {
                                        try { ct.WaitHandle.WaitOne(slotWaitMs); }
                                        catch { break; }
                                    }
                                }
                            }
                            if (ct.IsCancellationRequested) break;
                            if (burstOk + burstLost == 0) break;

                            // Physical gateway ICMP check (separates Wi-Fi/router drops from VPN/driver)
                            bool gwOkNow = false;
                            if (gwIp != null && !gwFiltered && totalSw.ElapsedMilliseconds < 60000)
                            {
                                gwSent++;
                                try
                                {
                                    var rep = ping.Send(gwIp, 350);
                                    if (rep != null && rep.Status == System.Net.NetworkInformation.IPStatus.Success)
                                    {
                                        gwOkNow = true;
                                        gwOkCount++;
                                        gwLatSum += Math.Max(1, (int)rep.RoundtripTime);
                                    }
                                    else gwLost++;
                                }
                                catch { gwLost++; }
                                if (!gwOkNow && gwSent >= 5 && gwOkCount == 0 && udpSent > udpLost)
                                    gwFiltered = true;
                            }

                            // VPN Endpoint check (marks "n/d" if filtered during the first iterations)
                            if (epIp != null && !epFiltered && totalSw.ElapsedMilliseconds < 60000)
                            {
                                bool gotEp = false;
                                try
                                {
                                    var epRep = ping.Send(epIp, 350);
                                    if (epRep != null && epRep.Status == System.Net.NetworkInformation.IPStatus.Success)
                                    {
                                        gotEp = true;
                                        epOkCount++;
                                        epLastOk = true;
                                        epLastMs = Math.Max(1, (int)epRep.RoundtripTime);
                                    }
                                }
                                catch { }

                                if (!gotEp)
                                {
                                    epLastOk = false;
                                    if (iter == 5 && epOkCount == 0 && cfgSnap != null)
                                    {
                                        NetDiag.ProbeResult tcpEp = NetDiag.ProbeEndpoint(cfgSnap, 800, ct);
                                        if (tcpEp != null && tcpEp.Ok)
                                        {
                                            epOkCount++;
                                            epLastOk = true;
                                            epLastMs = tcpEp.LatencyMs;
                                        }
                                        else if (gwIp == null || gwOkNow || (effMode == AppMode.All && tunnelRunning))
                                        {
                                            epFiltered = true;
                                        }
                                    }
                                }
                            }

                            if (ct.IsCancellationRequested) break;

                            bool fullSecDown = burstOk == 0;
                            if (fullSecDown)
                            {
                                consecDownSecs++;
                                if (consecDownSecs == 2) drops++;
                            }
                            else
                            {
                                if (consecDownSecs == 1) microDrops++; // brief 1s outage recovered
                                consecDownSecs = 0;
                                if (burstLost > 0 || burstSpike250) microDrops++;
                            }

                            bool gwBlockedInAll = effMode == AppMode.All && tunnelRunning;
                            if ((burstLost > 0 || fullSecDown) && (lastEventLogTick == 0 || ElapsedSeconds(lastEventLogTick) >= 5))
                            {
                                lastEventLogTick = NowTicks();
                                string reason;
                                if (gwIp != null && !gwFiltered && !gwOkNow && gwOkCount > 0 && !epLastOk && !gwBlockedInAll)
                                    reason = "Perda UDP + gateway físico falhou -> oscilação na rede local/Wi-Fi";
                                else if (epIp != null && !epFiltered && !epLastOk)
                                    reason = "Perda UDP + endpoint falhou (gateway OK) -> falha na internet externa ou servidor VPN";
                                else if (effMode == AppMode.Only && tunnelRunning)
                                    reason = usingTunThisSec
                                        ? "Microqueda UDP no túnel Só estes (" + burstLost + "/5 perdidos) -> instabilidade no túnel ou rota"
                                        : "Microqueda UDP (" + burstLost + "/5 perdidos; gateway OK) -> perda no bypass do driver WireSock";
                                else
                                    reason = "Microqueda UDP (" + burstLost + "/5 perdidos; " + (gwBlockedInAll ? "túnel All" : "gateway OK") + ") -> perda no túnel/rota";
                                NetMon.Record("Teste", fullSecDown ? NetMon.EventKind.Error : NetMon.EventKind.Warn, reason);
                            }

                            int avgMs = 0, maxMs = 0;
                            if (latencies.Count > 0)
                            {
                                long sum = 0;
                                for (int i = 0; i < latencies.Count; i++)
                                {
                                    int val = latencies[i];
                                    sum += val;
                                    if (val > maxMs) maxMs = val;
                                }
                                avgMs = (int)Math.Round((double)sum / latencies.Count);
                            }

                            int curSec = Math.Min(60, Math.Max(1, (int)Math.Round(totalSw.Elapsed.TotalSeconds)));
                            int curSent = udpSent, curLost = udpLost;
                            int curMicro = microDrops, curDrops = drops, curSpikes = spikes200;
                            int latCount = latencies.Count, curAvg = avgMs, curMax = maxMs;
                            int curJitter = (int)Math.Round(rfcJitter);
                            string gwText = gwIp == null ? "indisp."
                                          : gwOkCount > 0 ? ((int)Math.Round((double)gwLatSum / gwOkCount) + "ms (" + gwLost + "p)")
                                          : gwBlockedInAll ? "filtrado (túnel)"
                                          : gwFiltered ? "n/d (ICMP)"
                                          : ("falhou (" + gwLost + "/" + gwSent + ")");
                            string epText = epIp == null ? "—"
                                          : epFiltered ? "n/d (ICMP)"
                                          : epLastOk ? (epLastMs + " ms")
                                          : "sem resp.";

                            bool iterCancelled = ct.IsCancellationRequested;
                            var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                            if (ui != null)
                            {
                                ui.BeginInvoke(new Action(() =>
                                {
                                    if (exiting || myContEpoch != diagContEpoch || iterCancelled) return;
                                    txtDiagLiveTime.Text = string.Format("{0:00}:{1:00}", curSec / 60, curSec % 60);
                                    txtDiagLiveStatus.Text = (usingTunThisSec ? "Rajada UDP túnel Só estes (" : "Rajada UDP 5 pps (") + curSec + "/60s)";
                                    int lossPct = curSent > 0 ? (int)Math.Round(100.0 * curLost / curSent) : 0;
                                    txtDiagSentLoss.Text = curSent + " / " + lossPct + "%";
                                    txtDiagPing.Text = latCount > 0 ? (curAvg + " / " + curMax + " ms") : "— / —";
                                    txtDiagJitter.Text = latCount >= 2 ? (curJitter + " ms / " + curSpikes) : (latCount == 1 ? "0 ms / " + curSpikes : "—");
                                    txtDiagDrops.Text = curMicro + " / " + curDrops;
                                    txtDiagGw.Text = gwText;
                                    txtDiagEp.Text = epText;
                                    if (WindowShowing && pageDiag.Visibility == Visibility.Visible) UpdateDiagTimeline();
                                }));
                            }

                            int waitMs = Math.Min(1000 - (int)secSw.ElapsedMilliseconds, 60000 - (int)totalSw.ElapsedMilliseconds);
                            if (waitMs > 0)
                            {
                                try { ct.WaitHandle.WaitOne(waitMs); }
                                catch { break; }
                            }
                        }
                    }

                    if (consecDownSecs == 1) microDrops++;

                    bool doneCancelled = ct.IsCancellationRequested;
                    var uiDone = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (uiDone != null)
                    {
                        uiDone.BeginInvoke(new Action(() =>
                        {
                            if (exiting || myContEpoch != diagContEpoch) return;
                            diagContinuousRunning = false;
                            btnDiagContinuous.Content = "Teste contínuo";
                            if (!doneCancelled)
                            {
                                string statusText = (drops == 0 && microDrops == 0 && udpLost == 0)
                                    ? "Concluído — estável (0% perda)"
                                    : "Concluído — " + microDrops + " micro, " + drops + " queda(s)";
                                txtDiagLiveStatus.Text = statusText;
                                txtDiagDrops.Text = microDrops + " / " + drops;
                                lastContSummary = "Teste contínuo (" + statusText + "): " +
                                                  "UDP=" + txtDiagSentLoss.Text +
                                                  " | Ping(méd/máx)=" + txtDiagPing.Text +
                                                  " | Jitter/Picos=" + txtDiagJitter.Text +
                                                  " | Micro/Quedas=" + txtDiagDrops.Text +
                                                  " | GW=" + txtDiagGw.Text +
                                                  " | Endpoint=" + txtDiagEp.Text;
                                UpdateDiagTimeline();
                            }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    NetMon.Record("Teste", NetMon.EventKind.Error, "Erro inesperado no teste contínuo: " + ex.Message);
                    var uiErr = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (uiErr != null)
                    {
                        uiErr.BeginInvoke(new Action(() =>
                        {
                            if (exiting || myContEpoch != diagContEpoch) return;
                            diagContinuousRunning = false;
                            if (btnDiagContinuous != null) btnDiagContinuous.Content = "Teste contínuo";
                            if (txtDiagLiveStatus != null) txtDiagLiveStatus.Text = "Interrompido por erro";
                        }));
                    }
                }
                finally
                {
                    try { myCts.Dispose(); } catch { }
                }
            }) { IsBackground = true, Name = "DiagContinuous" };
            diagContThread.Start();
        }

        void StopContinuousDiag(string statusText)
        {
            diagContEpoch++;
            if (diagContCts != null)
            {
                try { diagContCts.Cancel(); } catch { }
                diagContCts = null;
            }
            if (!diagContinuousRunning) return;
            diagContinuousRunning = false;
            if (btnDiagContinuous != null) btnDiagContinuous.Content = "Teste contínuo";
            if (txtDiagLiveStatus != null && !string.IsNullOrEmpty(statusText)) txtDiagLiveStatus.Text = statusText;
        }

        void RepairDiagNetwork()
        {
            if (exiting) return;
            if (wasRunning == true && !ConfirmLiveInterruption("Reparar a rede durante a chamada?",
                    "O reparo pode reiniciar o túnel e derrubar a chamada ou a transmissão por alguns segundos.", "Reparar"))
                return;
            StopContinuousDiag("Interrompido para reparo");
            pendingPostHealDiagNote = null;

            NetDiag.MtuResult mtuSnap = lastDiagMtu;
            NetDiag.ConflictReport confSnap = lastDiagConflicts;
            bool dnsFailedSnap = lastDiagDnsFailed;
            bool logMtuSnap = lastDiagLogMtuErr;

            bool isolatedExitLag = false;
            if (confSnap != null && confSnap.ExitLagGameOverlap && confSnap.OverlappingGames.Count > 0 && active != null)
            {
                AppMode curEff = EffectiveAppMode();
                string gamesList = string.Join(", ", confSnap.OverlappingGames.ToArray());
                if (curEff == AppMode.Only)
                {
                    if (Dialogs.Confirm(w, "Isolar jogos do ExitLag?",
                        "O ExitLag e a VPN estão configurados para otimizar ao mesmo tempo:\n\n\u2022 " + gamesList +
                        "\n\nDeseja remover esses jogos de 'Só estes' (deixando o ExitLag cuidar deles e a VPN nos demais apps)?",
                        "Isolar jogos", true))
                    {
                        var removeSet = new HashSet<string>(confSnap.OverlappingGames, StringComparer.OrdinalIgnoreCase);
                        List<string> kept = new List<string>();
                        IList<string> sourceApps = settings.SharedApps ? (IList<string>)settings.Apps : (IList<string>)active.Apps;
                        for (int i = 0; i < sourceApps.Count; i++)
                        {
                            string a = sourceApps[i];
                            string clean = a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a.Substring(0, a.Length - 4) : a;
                            if (!removeSet.Contains(a) && !removeSet.Contains(clean)) kept.Add(a);
                        }
                        if (kept.Count == 0)
                        {
                            Dialogs.Info(w, "Lista ficaria vazia",
                                "Todos os apps em 'Só estes' são jogos do ExitLag (" + gamesList + "). Remova-os manualmente ou mude o modo para 'Todos, exceto'.");
                        }
                        else
                        {
                            try
                            {
                                ConfFile.WriteApps(Paths.Conf(active.Name), AppMode.Only, kept);
                                active = ConfigInfo.Read(active.Name);
                                if (settings.SharedApps) { settings.AppsMode = AppMode.Only; settings.Apps = kept; settings.Save(); }
                                LoadAppsEditor();
                                appsNeedReconnect = wasRunning == true;
                                isolatedExitLag = true;
                                NetMon.Record("Autocura", NetMon.EventKind.Info, "Jogos removidos de 'Só estes' para evitar conflito com ExitLag: " + gamesList);
                            }
                            catch (Exception ex)
                            {
                                NetMon.Record("Autocura", NetMon.EventKind.Error, "Falha ao atualizar apps sem jogos: " + ex.Message);
                                Dialogs.Info(w, "Falha ao atualizar apps", "Não foi possível salvar a lista sem os jogos:\n\n" + ex.Message);
                            }
                        }
                    }
                }
                else if (curEff == AppMode.All)
                {
                    if (Dialogs.Confirm(w, "Isolar jogos para o ExitLag?",
                        "O ExitLag está rodando junto com o modo 'Todos os apps' para:\n\n\u2022 " + gamesList +
                        "\n\nDeseja mudar para 'Todos, exceto' excluindo esses jogos da VPN?",
                        "Excluir jogos da VPN", true))
                    {
                        var excluded = new List<string>(confSnap.OverlappingGames);
                        try
                        {
                            ConfFile.WriteApps(Paths.Conf(active.Name), AppMode.Except, excluded);
                            active = ConfigInfo.Read(active.Name);
                            if (settings.SharedApps)
                            {
                                settings.AppsMode = AppMode.Except;
                                settings.Apps = excluded;
                                settings.Save();
                            }
                            LoadAppsEditor();
                            appsNeedReconnect = wasRunning == true;
                            isolatedExitLag = true;
                            NetMon.Record("Autocura", NetMon.EventKind.Info, "Modo alterado para 'Exceto' excluindo jogos do ExitLag: " + gamesList);
                        }
                        catch (Exception ex)
                        {
                            NetMon.Record("Autocura", NetMon.EventKind.Error, "Falha ao excluir jogos do ExitLag: " + ex.Message);
                            Dialogs.Info(w, "Falha ao atualizar apps", "Não foi possível aplicar a exclusão dos jogos:\n\n" + ex.Message);
                        }
                    }
                }
            }
            if (isolatedExitLag && lastDiagFixableCount <= 1 && !dnsFailedSnap && !logMtuSnap)
            {
                if (wasRunning == true) Reconnect();
                else RunQuickDiag("Jogos isolados do ExitLag");
                return;
            }

            bool stopExternal = false;
            List<string> extSvcs = confSnap != null ? confSnap.ConflictingServices : null;
            if (extSvcs != null && extSvcs.Count > 0)
            {
                bool onlyExtSvcs = lastDiagFixableCount > 0 && lastDiagFixableCount <= extSvcs.Count;
                if (onlyExtSvcs)
                {
                    if (!Dialogs.Confirm(w, "Encerrar serviços conflitantes?",
                        "Encontramos estes serviços em execução que causam quedas em jogos/VPN:\n\n\u2022 " +
                        string.Join("\n\u2022 ", extSvcs.ToArray()) +
                        "\n\nDeseja tentar encerrá-los temporariamente agora?",
                        "Encerrar serviços", true))
                        return;
                    stopExternal = true;
                }
                else
                {
                    stopExternal = Dialogs.Confirm(w, "Incluir serviços conflitantes?",
                        "Além dos ajustes de rede/MTU/WireSock, estes serviços estão em execução:\n\n\u2022 " +
                        string.Join("\n\u2022 ", extSvcs.ToArray()) +
                        "\n\nClique em \u201cIncluir serviços\u201d para encerrá-los junto, ou \u201cCancelar\u201d para corrigir apenas os demais problemas.",
                        "Incluir serviços", true);
                }
            }

            if (connecting || reconnectPending || (reconnectTimer != null && reconnectTimer.IsEnabled))
            {
                bool ok = Dialogs.Confirm(w, "Conexão em andamento",
                    "Para limpar o bloqueio de rede e rotas com segurança, o VPN Panel precisa encerrar a tentativa atual primeiro.",
                    "Encerrar e corrigir", true);
                if (!ok) return;
                CancelQuickDiag(true);
                diagRepairRunning = true;
                btnDiagRepair.IsEnabled = false;
                btnDiagRepair.Content = "Corrigindo...";
                SetDiagDot(diagVerdictDot, "Muted");
                txtDiagVerdict.Foreground = B("Text");
                txtDiagVerdict.Text = "Desconectando para corrigir...";
                txtDiagVerdictSub.Text = "Encerrando tentativa de túnel antes de aplicar as correções...";
                DisconnectAndResetLock(() => ExecuteAutoHealer(mtuSnap, confSnap, dnsFailedSnap, logMtuSnap, false, stopExternal));
                return;
            }

            ExecuteAutoHealer(mtuSnap, confSnap, dnsFailedSnap, logMtuSnap, wasRunning == true, stopExternal);
        }

        void ExecuteAutoHealer(NetDiag.MtuResult mtuSnap, NetDiag.ConflictReport confSnap, bool dnsFailedSnap, bool logMtuSnap, bool tunnelRunningNow, bool stopExternal)
        {
            if (exiting) return;
            CancelQuickDiag(true);
            diagRepairRunning = true;
            btnDiagRepair.IsEnabled = false;
            btnDiagRepair.Content = "Corrigindo...";
            SetDiagDot(diagVerdictDot, "Muted");
            txtDiagVerdict.Foreground = B("Text");
            txtDiagVerdict.Text = "Aplicando correções...";
            txtDiagVerdictSub.Text = "Limpando bloqueios/rotas, verificando serviços e ajustando MTU...";
            NetMon.Record("Autocura", NetMon.EventKind.Info, "Correção manual executada na aba Testes");

            bool optMtuSnap = settings.OptimizeMtu;
            int curMaxMtuSnap = DiagEffectiveMtu(tunnelRunningNow, EffectiveAppMode(), settings);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AutoHealer.FixReport report = null;
                try
                {
                    report = AutoHealer.ApplyFixes(
                        optMtuSnap,
                        curMaxMtuSnap,
                        mtuSnap,
                        confSnap,
                        logMtuSnap,
                        dnsFailedSnap,
                        tunnelRunningNow,
                        stopExternal,
                        () => exiting || Tunnel.Connecting || reconnectPending || (!tunnelRunningNow && wasRunning == true));
                }
                catch (Exception ex)
                {
                    report = new AutoHealer.FixReport();
                    report.Failed.Add(ex.Message);
                }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui == null) return;
                ui.BeginInvoke(new Action(() =>
                {
                    if (exiting) return;
                    diagRepairRunning = false;
                    if (btnDiagQuick != null) btnDiagQuick.IsEnabled = true;
                    btnDiagRepair.IsEnabled = true;
                    btnDiagRepair.Content = "Corrigir problemas encontrados";
                    ApplyHealReportToSettings(report);
                    if (report != null && report.SuggestedMtu > 0 && tunnelRunningNow)
                        bgMtuReducedThisSession = true;
                    CheckIp();

                    var parts = new List<string>();
                    if (report != null && report.Applied.Count > 0) parts.Add(string.Join("; ", report.Applied.ToArray()));
                    if (report != null && report.Failed.Count > 0) parts.Add("Falhas: " + string.Join("; ", report.Failed.ToArray()));
                    string fixNote = parts.Count > 0 ? string.Join(" | ", parts.ToArray()) : "Nenhuma alteração extra necessária";

                    SetDiagDot(diagVerdictDot, (report != null && report.Failed.Count > 0) ? "Warn" : "SecureText");
                    txtDiagVerdict.Foreground = (report != null && report.Failed.Count > 0) ? B("Warn") : B("Text");
                    txtDiagVerdict.Text = (report != null && report.FixedCount > 0) ? "Correções aplicadas (" + report.FixedCount + ")" : "Verificação de reparo concluída";
                    txtDiagVerdictSub.Text = fixNote;

                    if (report != null && report.NeedReconnect && wasRunning == true)
                    {
                        string dlgDetail = report.Applied.Count > 0
                            ? "Correções aplicadas:\n\u2022 " + string.Join("\n\u2022 ", report.Applied.ToArray())
                            : "Ajustes preparados.";
                        bool rec = Dialogs.Confirm(w, "Reconexão necessária",
                            dlgDetail + "\n\nDeseja reconectar a VPN agora para aplicar o filtro/MTU (" +
                            (EffectiveMaxMtu() > 0 ? EffectiveMaxMtu() + " B" : "padrão") + ")?",
                            "Reconectar agora", false);
                        if (rec)
                        {
                            pendingPostHealDiagNote = fixNote;
                            txtDiagVerdict.Text = "Reconectando para aplicar...";
                            txtDiagVerdictSub.Text = fixNote;
                            Reconnect();
                            return;
                        }
                    }

                    RunQuickDiag(fixNote);
                }));
            });
        }

        void CopyDiagReport()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(lastDiagSummary))
            {
                sb.AppendLine(lastDiagSummary);
            }
            else
            {
                sb.AppendLine("VPN Panel v" + Program.Version + " — Diagnóstico");
                sb.AppendLine("Veredito: " + (txtDiagVerdict != null ? txtDiagVerdict.Text : ""));
                if (txtChk1Detail != null) sb.AppendLine("1. Internet Base: " + txtChk1Detail.Text);
                if (txtChk2Detail != null) sb.AppendLine("2. DNS do Windows: " + txtChk2Detail.Text);
                if (txtChk3Detail != null) sb.AppendLine("3. Túnel & Servidor VPN: " + txtChk3Detail.Text);
                if (txtChk4Detail != null) sb.AppendLine("4. Kill Switch & IPv6: " + txtChk4Detail.Text);
                if (txtChk5Detail != null) sb.AppendLine("5. Configuração & Apps: " + txtChk5Detail.Text);
                if (txtChk6Detail != null) sb.AppendLine("6. MTU & Pacotes de Jogos: " + txtChk6Detail.Text);
                if (txtChk7Detail != null) sb.AppendLine("7. Conflitos & Driver: " + txtChk7Detail.Text);
            }
            if (!string.IsNullOrEmpty(lastContSummary))
            {
                sb.AppendLine(lastContSummary);
            }
            else if (txtDiagSentLoss != null && txtDiagSentLoss.Text != "—" && txtDiagSentLoss.Text != "0 / 0%")
            {
                sb.AppendLine("Teste contínuo (" + txtDiagLiveStatus.Text + "): " +
                              "UDP=" + txtDiagSentLoss.Text +
                              " | Ping(méd/máx)=" + txtDiagPing.Text +
                              " | Jitter/Picos=" + txtDiagJitter.Text +
                              " | Micro/Quedas=" + txtDiagDrops.Text +
                              " | GW=" + txtDiagGw.Text +
                              " | Endpoint=" + txtDiagEp.Text);
            }
            string timeline = NetMon.ExportText(30);
            if (!string.IsNullOrEmpty(timeline))
            {
                sb.AppendLine();
                sb.AppendLine("Linha do Tempo (últimos 30 eventos):");
                sb.AppendLine(timeline);
            }

            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
                btnDiagCopy.Content = "Copiado!";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
                t.Tick += (s, e) => { t.Stop(); if (!exiting && btnDiagCopy != null) btnDiagCopy.Content = "Copiar resultado"; };
                t.Start();
            }
            catch { }
        }
    }
}
