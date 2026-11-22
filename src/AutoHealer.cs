// VPN Panel - AutoHealer: automatic network repairs.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Shell;

namespace VpnPanel
{
    // Deterministic self-healing and safe auto-remediation for network lock, ghost IPv6,
    // conflicting WireSock/VPN services, orphan processes, and adaptive MTU step-down.
    static class AutoHealer
    {
        public class FixReport
        {
            public int FixedCount;
            public bool NeedReconnect;
            public bool EnableOptimizeMtu;
            public int SuggestedMtu;
            public List<string> Applied = new List<string>();
            public List<string> Skipped = new List<string>();
            public List<string> Failed = new List<string>();
        }

        static readonly object fixLock = new object();
        static DateTime lastBgNetHealUtc = DateTime.MinValue;
        static DateTime lastBgMtuHealUtc = DateTime.MinValue;

        static readonly HashSet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wuauserv", "bits", "dnscache", "dhcp", "nsi", "netprofm", "nlasvc",
            "bfe", "mpssvc", "iphlpsvc", "winmgmt", "rpcss", "dcomlaunch",
            "plugplay", "eventlog", "cryptsvc", "trustedinstaller",
            "lanmanworkstation", "lanmanserver", "netman", "wlansvc", "dot3svc",
            "rasman", "sstpsvc", "ikeext", "policyagent", "gpsvc", "profsvc",
            "samss", "lsass", "schedule", "winhttpautoproxysvc", "tcpip",
            "afd", "ndis", "ndiswgc", "wscsvc", "winrm", "windefend",
            "sense", "securityhealthservice", "warpjitsvc"
        };

        public static int SuggestSafeMtu(NetDiag.MtuResult mtu, bool logMtuErr, int currentMax)
        {
            if (!logMtuErr && currentMax > 0 && mtu != null && mtu.Ok && mtu.MaxPayload >= currentMax - 28)
                return 0;

            bool icmpBlockedOrCanceled = mtu != null && !mtu.Ok && mtu.Detail != null &&
                (mtu.Detail.IndexOf("ICMP bloqueado", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 mtu.Detail.Equals("Cancelado", StringComparison.OrdinalIgnoreCase));
            bool fragmented = mtu != null && (
                (mtu.Ok && mtu.MaxPayload > 0 && mtu.MaxPayload < 1472) ||
                (!mtu.Ok && !icmpBlockedOrCanceled && mtu.Detail != null &&
                 mtu.Detail.IndexOf("Fragmenta", StringComparison.OrdinalIgnoreCase) >= 0));

            if (!fragmented && !logMtuErr) return 0;

            if (mtu != null && !mtu.Ok && fragmented)
                return 1280;

            if (mtu != null && mtu.Ok && mtu.MaxPayload > 0 && mtu.MaxPayload < 1392)
            {
                // If 1352 passed only because the tunnel itself was capped at 1380, step 1380 -> 1340 first.
                if (currentMax >= 1380 && mtu.MaxPayload >= currentMax - 28)
                    return 1340;
                return 1280;
            }

            if (currentMax > 0 && currentMax <= 1340) return 1280;
            return 1340;
        }

        public static List<string> StopConflictingServices(List<string> targets)
        {
            var stopped = new List<string>();
            if (targets == null || targets.Count == 0) return stopped;

            var targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++)
            {
                string t = targets[i];
                if (!string.IsNullOrEmpty(t)) targetSet.Add(t.Trim());
            }
            if (targetSet.Count == 0) return stopped;

            System.ServiceProcess.ServiceController[] services = null;
            try
            {
                services = System.ServiceProcess.ServiceController.GetServices();
                for (int i = 0; i < services.Length; i++)
                {
                    System.ServiceProcess.ServiceController s = services[i];
                    try
                    {
                        if (s.Status != System.ServiceProcess.ServiceControllerStatus.Running) continue;
                        if (s.ServiceName.StartsWith("wiresock", StringComparison.OrdinalIgnoreCase)) continue;
                        if (ProtectedServices.Contains(s.ServiceName)) continue;

                        bool matched = targetSet.Contains(s.ServiceName) ||
                            (!string.IsNullOrEmpty(s.DisplayName) && targetSet.Contains(s.DisplayName));
                        if (!matched) continue;
                        if (!s.CanStop) continue;

                        s.Stop();
                        s.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(4));
                        if (s.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                        {
                            string label = string.IsNullOrEmpty(s.DisplayName) ? s.ServiceName : s.DisplayName;
                            if (!stopped.Contains(label)) stopped.Add(label);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                if (services != null)
                {
                    for (int i = 0; i < services.Length; i++)
                    {
                        try { services[i].Dispose(); } catch { }
                    }
                }
            }
            return stopped;
        }

        public static FixReport ApplyFixes(
            Settings settings,
            NetDiag.MtuResult mtuRes,
            NetDiag.ConflictReport conflicts,
            bool logMtuErr,
            bool dnsFailed,
            bool tunnelRunning,
            bool stopExternalServices)
        {
            bool opt = settings == null || settings.OptimizeMtu;
            int cur = settings != null && settings.AdaptiveMtu > 0 ? settings.AdaptiveMtu : (opt && tunnelRunning ? 1380 : 0);
            return ApplyFixes(opt, cur, mtuRes, conflicts, logMtuErr, dnsFailed, tunnelRunning, stopExternalServices, null);
        }

        public static FixReport ApplyFixes(
            bool optimizeMtu,
            int currentMaxMtu,
            NetDiag.MtuResult mtuRes,
            NetDiag.ConflictReport conflicts,
            bool logMtuErr,
            bool dnsFailed,
            bool tunnelRunning,
            bool stopExternalServices,
            Func<bool> isBusy)
        {
            var report = new FixReport();
            lock (fixLock)
            {
                // 1. Stop conflicting native WireSock service if running
                bool wsSvc = false;
                try { wsSvc = Paths.WireSockServiceRunning; } catch { }
                if (wsSvc)
                {
                    try
                    {
                        Paths.StopWireSockService();
                        if (!Paths.WireSockServiceRunning)
                        {
                            report.Applied.Add("Serviço nativo do WireSock encerrado");
                            NetMon.Record("Autocura", NetMon.EventKind.Info, "Serviço nativo do WireSock encerrado.");
                        }
                        else
                        {
                            report.Failed.Add("Não foi possível encerrar o serviço nativo do WireSock");
                            NetMon.Record("Autocura", NetMon.EventKind.Warn, "Não foi possível encerrar o serviço nativo do WireSock.");
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Failed.Add("Erro ao encerrar serviço WireSock: " + ex.Message);
                        NetMon.Record("Autocura", NetMon.EventKind.Warn, "Erro ao encerrar serviço WireSock: " + ex.Message);
                    }
                }

                // 2. Kill orphans + release WFP/runtime + flush DNS/ghost IPv6 serialized under Tunnel.stopLock
                if (!tunnelRunning)
                {
                    int orphansKilled;
                    bool lockReset;
                    bool ghostOrDnsCleaned;
                    bool cleaned = Tunnel.TryIdleCleanup(
                        true,
                        true,
                        dnsFailed,
                        true,
                        isBusy,
                        out orphansKilled,
                        out lockReset,
                        out ghostOrDnsCleaned);

                    if (!cleaned)
                    {
                        report.Skipped.Add("Limpeza de rede ignorada porque há uma conexão em andamento");
                        NetMon.Record("Autocura", NetMon.EventKind.Info, "Limpeza de rede ignorada (túnel em início/operação).");
                    }
                    else
                    {
                        if (orphansKilled > 0)
                        {
                            report.Applied.Add("Processos órfãos do WireSock encerrados (" + orphansKilled + ")");
                            NetMon.Record("Autocura", NetMon.EventKind.Info, "Processos órfãos do WireSock encerrados: " + orphansKilled + ".");
                        }
                        if (lockReset)
                        {
                            report.Applied.Add("Kill Switch residual (WFP) e arquivos temporários limpos");
                            NetMon.Record("Autocura", NetMon.EventKind.Info, "WFP/Kill Switch residual e arquivos temporários limpos.");
                        }
                        if (ghostOrDnsCleaned)
                        {
                            report.Applied.Add("Cache DNS renovado e rotas IPv6 fantasmas limpas");
                            NetMon.Record("Autocura", NetMon.EventKind.Info, "Cache DNS renovado e rotas IPv6 fantasmas limpas.");
                        }
                    }
                    List<string> restoredDns;
                    if (RestoreStuckPhysicalDns(out restoredDns))
                    {
                        report.Applied.Add("DNS preso em IP de VPN restaurado para DHCP em: " + string.Join(", ", restoredDns.ToArray()));
                        NetMon.Record("Autocura", NetMon.EventKind.Warn, "DNS de VPN preso na placa física restaurado: " + string.Join(", ", restoredDns.ToArray()));
                    }
                }
                else
                {
                    if (conflicts != null && conflicts.WireSockProcessCount > 1)
                    {
                        report.Skipped.Add("Múltiplos wiresock-client.exe com túnel ativo (reconecte para limpar)");
                        report.NeedReconnect = true;
                    }
                    bool hasGhostV6 = false;
                    try { hasGhostV6 = ConfFile.HasGhostIpv6(); } catch { }
                    if (dnsFailed || hasGhostV6)
                    {
                        ConfFile.CleanGhostNetwork(true);
                        report.Applied.Add("Cache DNS e rotas IPv6 fantasmas limpos (túnel mantido)");
                        NetMon.Record("Autocura", NetMon.EventKind.Info, "Cache DNS e rotas IPv6 fantasmas limpos com túnel ativo.");
                    }
                    if (Tunnel.HasPendingNetworkLock)
                    {
                        report.Skipped.Add("Reset completo de WFP adiado enquanto o túnel está ativo");
                        report.NeedReconnect = true;
                    }
                }

                // 3. Adaptive MTU recommendation (UI thread applies to Settings and calls Save)
                int suggested = SuggestSafeMtu(mtuRes, logMtuErr, currentMaxMtu);
                if (suggested > 0)
                {
                    if (!optimizeMtu || currentMaxMtu == 0 || suggested < currentMaxMtu)
                    {
                        report.SuggestedMtu = suggested;
                        report.EnableOptimizeMtu = true;
                        report.Applied.Add("MTU adaptativo configurado para " + suggested + " bytes");
                        NetMon.Record("Autocura", NetMon.EventKind.Info, "MTU adaptativo ajustado para " + suggested + ".");
                        if (tunnelRunning) report.NeedReconnect = true;
                    }
                    else if (currentMaxMtu > 0)
                    {
                        report.Skipped.Add("MTU adaptativo já está no piso seguro (" + currentMaxMtu + " bytes)");
                    }
                }

                // 4. External conflicting services only when explicitly authorized by user
                if (conflicts != null && conflicts.ConflictingServices != null && conflicts.ConflictingServices.Count > 0)
                {
                    if (stopExternalServices)
                    {
                        List<string> stopped = StopConflictingServices(conflicts.ConflictingServices);
                        for (int i = 0; i < stopped.Count; i++)
                        {
                            report.Applied.Add("Serviço conflitante encerrado: " + stopped[i]);
                            NetMon.Record("Autocura", NetMon.EventKind.Info, "Serviço conflitante encerrado: " + stopped[i] + ".");
                        }
                        for (int i = 0; i < conflicts.ConflictingServices.Count; i++)
                        {
                            string tgt = conflicts.ConflictingServices[i];
                            bool wasStopped = false;
                            for (int j = 0; j < stopped.Count; j++)
                            {
                                if (stopped[j].Equals(tgt, StringComparison.OrdinalIgnoreCase))
                                {
                                    wasStopped = true;
                                    break;
                                }
                            }
                            if (!wasStopped)
                            {
                                report.Failed.Add("Não foi possível encerrar: " + tgt);
                                NetMon.Record("Autocura", NetMon.EventKind.Warn, "Não foi possível encerrar serviço conflitante: " + tgt + ".");
                            }
                        }
                    }
                    else
                    {
                        report.Skipped.Add("Serviços concorrentes mantidos sem alteração: " +
                            string.Join(", ", conflicts.ConflictingServices.ToArray()));
                    }
                }

                report.FixedCount = report.Applied.Count;
                return report;
            }
        }

        public static FixReport TryBackgroundAutoHeal(Settings settings, bool tunnelRunning, bool repeatedMtuError)
        {
            return TryBackgroundAutoHeal(settings, tunnelRunning, repeatedMtuError, null);
        }

        public static FixReport TryBackgroundAutoHeal(Settings settings, bool tunnelRunning, bool repeatedMtuError, NetDiag.MtuResult mtuRes)
        {
            if (settings == null || !settings.AutoHeal) return new FixReport();
            int cur = settings.AdaptiveMtu > 0 ? settings.AdaptiveMtu : (settings.OptimizeMtu && tunnelRunning ? 1380 : 0);
            return TryBackgroundAutoHeal(settings.AutoHeal, settings.OptimizeMtu, cur, tunnelRunning, repeatedMtuError, true, mtuRes, null);
        }

        public static FixReport TryBackgroundAutoHeal(
            bool autoHealEnabled,
            bool optimizeMtu,
            int currentMaxMtu,
            bool tunnelRunning,
            bool repeatedMtuError,
            bool allowMtuStep,
            NetDiag.MtuResult mtuRes,
            Func<bool> isBusy)
        {
            var report = new FixReport();
            if (!autoHealEnabled) return report;

            bool lockTaken = Monitor.TryEnter(fixLock, 250);
            if (!lockTaken) return report;
            try
            {
                DateTime now = DateTime.UtcNow;

                if (!tunnelRunning && (lastBgNetHealUtc == DateTime.MinValue || (now - lastBgNetHealUtc).TotalSeconds >= 60.0))
                {
                    int orphansKilled;
                    bool lockReset;
                    bool ghostOrDnsCleaned;
                    bool cleaned = Tunnel.TryIdleCleanup(
                        false,
                        true,
                        false,
                        true,
                        isBusy,
                        out orphansKilled,
                        out lockReset,
                        out ghostOrDnsCleaned);

                    if (cleaned)
                    {
                        lastBgNetHealUtc = now;
                        if (lockReset)
                        {
                            report.Applied.Add("Kill Switch residual (WFP) e temporários limpos automaticamente");
                            NetMon.Record("Autocura", NetMon.EventKind.Warn, "Kill Switch residual (WFP) e temporários limpos em segundo plano.");
                        }
                        if (ghostOrDnsCleaned)
                        {
                            report.Applied.Add("Rotas IPv6 fantasmas e cache DNS limpos automaticamente");
                            NetMon.Record("Autocura", NetMon.EventKind.Warn, "Rotas IPv6 fantasmas e cache DNS limpos em segundo plano.");
                        }
                    }
                    List<string> restoredDns;
                    if (RestoreStuckPhysicalDns(out restoredDns))
                    {
                        report.Applied.Add("DNS preso de VPN restaurado para automático em: " + string.Join(", ", restoredDns.ToArray()));
                        NetMon.Record("Autocura", NetMon.EventKind.Warn, "DNS preso em placa física restaurado em segundo plano: " + string.Join(", ", restoredDns.ToArray()));
                    }
                }

                if (repeatedMtuError && allowMtuStep && optimizeMtu &&
                    (lastBgMtuHealUtc == DateTime.MinValue || (now - lastBgMtuHealUtc).TotalSeconds >= 60.0))
                {
                    lastBgMtuHealUtc = now;

                    int suggested = SuggestSafeMtu(mtuRes, true, currentMaxMtu);
                    if (suggested > 0 && (currentMaxMtu == 0 || suggested < currentMaxMtu))
                    {
                        report.SuggestedMtu = suggested;
                        if (tunnelRunning) report.NeedReconnect = true;
                        report.Applied.Add("MTU adaptativo reduzido automaticamente para " + suggested + " bytes");
                        NetMon.Record("Autocura", NetMon.EventKind.Warn,
                            "MTU adaptativo ajustado automaticamente para " + suggested + " após erros repetidos de MTU no log.");
                    }
                }

                report.FixedCount = report.Applied.Count;
                return report;
            }
            finally
            {
                Monitor.Exit(fixLock);
            }
        }

        // Unconditional post-drop recovery: cleans runtime copies, flushes Windows DNS negative cache,
        // purges ghost IPv6, and resets WFP/network-lock if either a pending lock exists OR base UDP/TCP
        // traffic fails despite an active physical gateway (network probes run outside fixLock so Connect never blocks).
        public static FixReport TryPostDropRecovery(bool networkLockConfigured, bool verifyIfNoPendingLock, Func<bool> isBusy)
        {
            var report = new FixReport();
            bool cleaned = false;
            bool lockReset = false;

            lock (fixLock)
            {
                int orphansKilled;
                bool ghostOrDnsCleaned;
                cleaned = Tunnel.TryIdleCleanup(
                    true,
                    true,
                    false,
                    true,
                    true,
                    isBusy,
                    out orphansKilled,
                    out lockReset,
                    out ghostOrDnsCleaned);

                if (!cleaned)
                {
                    report.Skipped.Add("Limpeza pós-queda ignorada porque uma nova conexão já iniciou");
                    return report;
                }

                if (orphansKilled > 0)
                {
                    report.Applied.Add("Processos órfãos do WireSock encerrados (" + orphansKilled + ")");
                    NetMon.Record("Autocura", NetMon.EventKind.Warn, "Processos órfãos encerrados após queda: " + orphansKilled + ".");
                }
                if (lockReset)
                {
                    report.Applied.Add("Kill Switch residual (WFP) liberado após queda");
                    NetMon.Record("Autocura", NetMon.EventKind.Warn, "WFP / Kill Switch residual e temporários limpos após queda.");
                }
                if (ghostOrDnsCleaned)
                {
                    report.Applied.Add("Cache DNS renovado e rotas IPv6 fantasmas limpas");
                    NetMon.Record("Autocura", NetMon.EventKind.Info, "Cache DNS renovado após encerramento do túnel.");
                }
                List<string> restoredDns;
                if (RestoreStuckPhysicalDns(out restoredDns))
                {
                    report.Applied.Add("DNS preso de VPN restaurado para DHCP em: " + string.Join(", ", restoredDns.ToArray()));
                    NetMon.Record("Autocura", NetMon.EventKind.Warn, "DNS preso na placa física restaurado após queda: " + string.Join(", ", restoredDns.ToArray()));
                }
                lastBgNetHealUtc = DateTime.UtcNow;
            }

            // Network probes run outside fixLock so manual Connect() / pre-connect heal is never blocked
            if (!lockReset && (networkLockConfigured || verifyIfNoPendingLock) &&
                (isBusy == null || !isBusy()) && NetDiag.HasPhysicalUplink())
            {
                bool trafficBlocked = false;
                if (!networkLockConfigured)
                {
                    NetDiag.ProbeResult udp = NetDiag.ProbeUdpDns("1.1.1.1", 53, 800, CancellationToken.None);
                    if ((udp == null || !udp.Ok) && (isBusy == null || !isBusy()))
                    {
                        NetDiag.ProbeResult tcp = NetDiag.ProbeTcp("1.1.1.1", 443, 800, CancellationToken.None);
                        if (tcp == null || !tcp.Ok) trafficBlocked = true;
                    }
                }

                if ((networkLockConfigured || trafficBlocked) && (isBusy == null || !isBusy()))
                {
                    lock (fixLock)
                    {
                        int extraOrphans;
                        bool extraReset, extraDns;
                        if (Tunnel.TryIdleCleanup(false, true, true, true, true, isBusy,
                            out extraOrphans, out extraReset, out extraDns) && extraReset)
                        {
                            report.Applied.Add("Reset preventivo de bloqueio/driver aplicado após queda");
                            NetMon.Record("Autocura", NetMon.EventKind.Warn,
                                trafficBlocked
                                    ? "Tráfego base falhou após queda com link físico ativo; reset preventivo do bloqueio WFP aplicado."
                                    : "Reset preventivo de bloqueio WFP aplicado após queda.");
                        }
                        lastBgNetHealUtc = DateTime.UtcNow;
                    }
                }
            }

            report.FixedCount = report.Applied.Count;
            return report;
        }

        static bool IsSameIpv4Subnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            if (a == null || b == null || mask == null) return false;
            if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
                return false;
            byte[] ba = a.GetAddressBytes(), bb = b.GetAddressBytes(), bm = mask.GetAddressBytes();
            for (int i = 0; i < 4; i++)
                if ((ba[i] & bm[i]) != (bb[i] & bm[i])) return false;
            return true;
        }

        sealed class TaintedDnsNic
        {
            public string Name;
            public readonly List<string> V4Ips = new List<string>();
            public readonly List<string> V6Ips = new List<string>();
        }

        static HashSet<string> ReadStaticDnsFromRegistry(string adapterId, bool ipv6)
        {
            var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(adapterId)) return res;
            try
            {
                string sub = (ipv6 ? @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\"
                                   : @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\") + adapterId;
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub))
                {
                    string raw = k != null ? k.GetValue("NameServer") as string : null;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        foreach (string part in raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            IPAddress ip;
                            if (IPAddress.TryParse(part.Trim(), out ip)) res.Add(ip.ToString());
                        }
                    }
                }
            }
            catch { }
            return res;
        }

        // Detects active physical adapters left with a statically configured private/ULA DNS IP
        // from a WireGuard config (ignoring DHCP-provided DNS, gateway IPs, and same-subnet LAN IPs).
        public static List<string> FindStuckVpnDnsAdapters(HashSet<string> vpnPrivateDns, NetworkInterface[] nics)
        {
            var names = new List<string>();
            List<TaintedDnsNic> details = ScanStuckVpnDnsAdapters(vpnPrivateDns, nics);
            for (int i = 0; i < details.Count; i++) names.Add(details[i].Name);
            return names;
        }

        static List<TaintedDnsNic> ScanStuckVpnDnsAdapters(HashSet<string> vpnPrivateDns, NetworkInterface[] nics)
        {
            var tainted = new List<TaintedDnsNic>();
            if (vpnPrivateDns == null || vpnPrivateDns.Count == 0 || nics == null) return tainted;
            foreach (NetworkInterface ni in nics)
            {
                try
                {
                    if (!NetDiag.IsPhysicalAdapter(ni) || string.IsNullOrEmpty(ni.Name)) continue;
                    HashSet<string> staticV4 = ReadStaticDnsFromRegistry(ni.Id, false);
                    HashSet<string> staticV6 = ReadStaticDnsFromRegistry(ni.Id, true);
                    if (staticV4.Count == 0 && staticV6.Count == 0) continue; // DHCP-managed DNS: never touch

                    IPInterfaceProperties props = ni.GetIPProperties();
                    if (props == null) continue;
                    var gws = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                        if (gw != null && gw.Address != null) gws.Add(gw.Address.ToString());

                    var entry = new TaintedDnsNic { Name = ni.Name };
                    foreach (IPAddress dns in props.DnsAddresses)
                    {
                        if (dns == null) continue;
                        string ds = dns.ToString();
                        if (!vpnPrivateDns.Contains(ds) || gws.Contains(ds)) continue;
                        if (dns.AddressFamily == AddressFamily.InterNetwork)
                        {
                            if (!staticV4.Contains(ds)) continue; // came from DHCP, not static
                            bool inLocalSubnet = false;
                            foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                            {
                                if (ua.Address != null && ua.IPv4Mask != null &&
                                    IsSameIpv4Subnet(dns, ua.Address, ua.IPv4Mask))
                                {
                                    inLocalSubnet = true;
                                    break;
                                }
                            }
                            if (!inLocalSubnet && !entry.V4Ips.Contains(ds)) entry.V4Ips.Add(ds);
                        }
                        else if (dns.AddressFamily == AddressFamily.InterNetworkV6 && staticV6.Contains(ds))
                        {
                            if (!entry.V6Ips.Contains(ds)) entry.V6Ips.Add(ds);
                        }
                    }
                    if ((entry.V4Ips.Count > 0 || entry.V6Ips.Count > 0) &&
                        !tainted.Any(x => x.Name.Equals(ni.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        tainted.Add(entry);
                    }
                }
                catch { }
            }
            return tainted;
        }

        public static bool RestoreStuckPhysicalDns(out List<string> restoredAdapters)
        {
            restoredAdapters = new List<string>();
            try
            {
                if (Tunnel.Connecting || Tunnel.Active(true) || Tunnel.IsWintunServiceRunning() ||
                    Paths.WireSockServiceRunning || NetDiag.HasExternalActiveVpnAdapter())
                    return false;
                HashSet<string> vpnDns = ConfFile.CollectVpnPrivateDns();
                if (vpnDns.Count == 0) return false;
                List<TaintedDnsNic> tainted = ScanStuckVpnDnsAdapters(vpnDns, NetworkInterface.GetAllNetworkInterfaces());
                for (int i = 0; i < tainted.Count; i++)
                {
                    string name = tainted[i].Name.Replace("\"", "");
                    string o, e;
                    bool removedAny = false;
                    for (int j = 0; j < tainted[i].V4Ips.Count; j++)
                    {
                        string ip = tainted[i].V4Ips[j].Replace("\"", "");
                        if (ProcUtil.RunBounded("netsh.exe", "interface ip delete dnsservers name=\"" + name + "\" address=" + ip + " validate=no", 4000, out o, out e) == 0)
                            removedAny = true;
                    }
                    for (int j = 0; j < tainted[i].V6Ips.Count; j++)
                    {
                        string ip = tainted[i].V6Ips[j].Replace("\"", "");
                        if (ProcUtil.RunBounded("netsh.exe", "interface ipv6 delete dnsservers name=\"" + name + "\" address=" + ip + " validate=no", 3000, out o, out e) == 0)
                            removedAny = true;
                    }
                    if (removedAny) restoredAdapters.Add(tainted[i].Name);
                }
                if (restoredAdapters.Count > 0) ConfFile.FlushDnsCache();
            }
            catch { }
            return restoredAdapters.Count > 0;
        }

        public static void RestoreDefaultInternet(
            out bool hasUplink,
            out NetDiag.ProbeResult udpProbe,
            out NetDiag.ProbeResult tcpProbe,
            out List<string> actions)
        {
            RestoreDefaultInternet(false, out hasUplink, out udpProbe, out tcpProbe, out actions);
        }

        // Unconditional emergency/guarantee restore: stops VPN Panel tunnel, removes all WFP locks/runtime copies,
        // clears ghost IPv6/DNS, restores stuck physical DNS, and verifies direct internet reachability.
        public static void RestoreDefaultInternet(
            bool stopExternalWireSockService,
            out bool hasUplink,
            out NetDiag.ProbeResult udpProbe,
            out NetDiag.ProbeResult tcpProbe,
            out List<string> actions)
        {
            actions = new List<string>();
            try
            {
                if (Tunnel.Active(true) || Tunnel.IsWintunServiceRunning())
                {
                    Tunnel.Stop(true);
                    actions.Add("Túnel VPN desconectado");
                }
            }
            catch { }
            try
            {
                if (stopExternalWireSockService && Paths.WireSockServiceRunning)
                {
                    Paths.StopWireSockService();
                    actions.Add("Serviço WireSock nativo encerrado");
                }
            }
            catch { }

            int orphansKilled;
            bool lockReset, ghostOrDnsCleaned;
            if (Tunnel.TryIdleCleanup(true, true, true, true, true, () => false,
                out orphansKilled, out lockReset, out ghostOrDnsCleaned))
            {
                if (orphansKilled > 0) actions.Add("Processos órfãos encerrados (" + orphansKilled + ")");
                if (lockReset) actions.Add("Bloqueio de rede WFP / Kill Switch resetado");
                if (ghostOrDnsCleaned) actions.Add("Rotas IPv6 fantasmas e cache DNS limpos");
            }
            else
            {
                Tunnel.ResetNetworkLock();
                ConfFile.CleanGhostNetwork(true);
                actions.Add("Bloqueio WFP e cache DNS resetados diretamente");
            }

            List<string> restoredDns;
            if (RestoreStuckPhysicalDns(out restoredDns))
                actions.Add("DNS restaurado para automático em: " + string.Join(", ", restoredDns.ToArray()));

            ConfFile.FlushDnsCache();
            hasUplink = NetDiag.HasPhysicalUplink();
            udpProbe = hasUplink ? NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, CancellationToken.None) : null;
            tcpProbe = hasUplink ? NetDiag.ProbeTcp("1.1.1.1", 443, 1500, CancellationToken.None) : null;
        }
    }
}
