// VPN Panel - NetDiag: passive diagnostics, MTU discovery, tunnel probes and conflict detection.

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
    // Passive network diagnostics and stability probing for the Tests tab.
    // Never modifies routes, WFP/network-lock state, or restarts the tunnel.
    static class NetDiag
    {
        public class ProbeResult
        {
            public bool Ok;
            public int LatencyMs; // -1 on failure
            public string Target;
            public string Detail;
        }

        public enum AuditSeverity { Ok, Warn, Error }

        public class AuditItem
        {
            public string Key;
            public AuditSeverity Severity;
            public string Title;
            public string Detail;
        }

        static string ShortErr(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "Sem resposta";
            string s = msg.Trim().Replace("\r", " ").Replace("\n", " ");
            return s.Length > 72 ? s.Substring(0, 72) + "..." : s;
        }

        public static ProbeResult ProbeHttp204(int timeoutMs, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = "cp.cloudflare.com", Detail = "Cancelado" };
            int timeout = Math.Max(500, timeoutMs);
            var sw = Stopwatch.StartNew();
            string httpErr = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://cp.cloudflare.com/generate_204");
                req.Method = "GET";
                req.UserAgent = "vpn-panel";
                req.Timeout = timeout;
                req.ReadWriteTimeout = timeout;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                using (ct.Register(() => { try { req.Abort(); } catch { } }))
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    sw.Stop();
                    int code = (int)resp.StatusCode;
                    if (code >= 200 && code < 400)
                    {
                        return new ProbeResult
                        {
                            Ok = true,
                            LatencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds),
                            Target = "cp.cloudflare.com",
                            Detail = "HTTP " + code
                        };
                    }
                    httpErr = "HTTP " + code;
                }
            }
            catch (WebException wex)
            {
                if (ct.IsCancellationRequested)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = "cp.cloudflare.com", Detail = "Cancelado" };
                httpErr = wex.Message;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = "cp.cloudflare.com", Detail = "Cancelado" };
                httpErr = ex.Message;
            }

            ProbeResult tcp = ProbeTcp("1.1.1.1", 443, Math.Min(timeout, 2500), ct);
            if (tcp.Ok)
            {
                return new ProbeResult
                {
                    Ok = true,
                    LatencyMs = tcp.LatencyMs,
                    Target = "1.1.1.1:443",
                    Detail = "TCP OK (HTTP falhou: " + ShortErr(httpErr) + ")"
                };
            }
            return new ProbeResult
            {
                Ok = false,
                LatencyMs = -1,
                Target = "cp.cloudflare.com",
                Detail = ShortErr(!string.IsNullOrEmpty(httpErr) ? httpErr : tcp.Detail)
            };
        }

        public static ProbeResult ProbeTcp(string host, int port, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(host))
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = "", Detail = "Host vazio" };
            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = host + ":" + port, Detail = "Cancelado" };

            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                ProbeResult dns = ProbeDns(host, ct);
                if (!dns.Ok || !IPAddress.TryParse(dns.Detail, out ip))
                {
                    return new ProbeResult
                    {
                        Ok = false,
                        LatencyMs = -1,
                        Target = host + ":" + port,
                        Detail = "DNS falhou: " + dns.Detail
                    };
                }
            }
            SocketError err;
            return ProbeTcpIp(ip, port, timeoutMs, ct, out err);
        }

        static ProbeResult ProbeTcpIp(IPAddress ip, int port, int timeoutMs, CancellationToken ct, out SocketError sockErr)
        {
            sockErr = SocketError.Success;
            string target = ip + ":" + port;
            int timeout = Math.Max(400, timeoutMs);
            var sw = Stopwatch.StartNew();
            Socket sock = null;
            var done = new ManualResetEventSlim(false);
            bool connected = false;
            string errMsg = null;
            SocketError localErr = SocketError.Success;
            try
            {
                sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                sock.BeginConnect(new IPEndPoint(ip, port), ar =>
                {
                    try
                    {
                        sock.EndConnect(ar);
                        connected = true;
                    }
                    catch (SocketException sex)
                    {
                        localErr = sex.SocketErrorCode;
                        errMsg = sex.Message;
                    }
                    catch (ObjectDisposedException)
                    {
                        localErr = SocketError.OperationAborted;
                    }
                    catch (Exception ex)
                    {
                        localErr = SocketError.SocketError;
                        errMsg = ex.Message;
                    }
                    finally
                    {
                        try { done.Set(); } catch { }
                    }
                }, null);

                bool signaled;
                try { signaled = done.Wait(timeout, ct); }
                catch (OperationCanceledException)
                {
                    sockErr = SocketError.OperationAborted;
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Cancelado" };
                }

                sw.Stop();
                sockErr = localErr;
                if (!signaled)
                {
                    sockErr = SocketError.TimedOut;
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Tempo esgotado" };
                }
                if (connected)
                {
                    return new ProbeResult
                    {
                        Ok = true,
                        LatencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds),
                        Target = target,
                        Detail = "TCP OK"
                    };
                }
                return new ProbeResult
                {
                    Ok = false,
                    LatencyMs = -1,
                    Target = target,
                    Detail = ShortErr(errMsg ?? sockErr.ToString())
                };
            }
            catch (Exception ex)
            {
                sockErr = SocketError.SocketError;
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = ShortErr(ex.Message) };
            }
            finally
            {
                if (sock != null) { try { sock.Close(); } catch { } }
                done.Dispose();
            }
        }

        public static ProbeResult ProbeDns(string host, CancellationToken ct)
        {
            string target = string.IsNullOrEmpty(host) ? "cloudflare.com" : host;
            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Cancelado" };

            var sw = Stopwatch.StartNew();
            IPAddress[] addrs = null;
            Exception dnsEx = null;
            var done = new ManualResetEventSlim(false);
            try
            {
                Dns.BeginGetHostAddresses(target, ar =>
                {
                    try { addrs = Dns.EndGetHostAddresses(ar); }
                    catch (Exception ex) { dnsEx = ex; }
                    finally { try { done.Set(); } catch { } }
                }, null);

                bool signaled;
                try { signaled = done.Wait(3000, ct); }
                catch (OperationCanceledException)
                {
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Cancelado" };
                }
                sw.Stop();
                if (!signaled)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Tempo esgotado no DNS" };
                if (dnsEx != null)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = ShortErr(dnsEx.Message) };
                if (addrs == null || addrs.Length == 0)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Nenhum IP retornado" };

                IPAddress valid = null;
                foreach (IPAddress a in addrs)
                {
                    if (!IPAddress.Any.Equals(a) && !IPAddress.IPv6Any.Equals(a))
                    {
                        valid = a;
                        break;
                    }
                }
                if (valid == null)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "DNS retornou 0.0.0.0 (bloqueio/falha)" };

                return new ProbeResult
                {
                    Ok = true,
                    LatencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds),
                    Target = target,
                    Detail = valid.ToString()
                };
            }
            catch (Exception ex)
            {
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = ShortErr(ex.Message) };
            }
            finally
            {
                done.Dispose();
            }
        }

        public static ProbeResult ProbeEndpoint(ConfigInfo cfg, int timeoutMs, CancellationToken ct)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.Endpoint))
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = "", Detail = "Nenhum servidor selecionado" };
            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = cfg.Endpoint, Detail = "Cancelado" };

            IPAddress ip;
            if (!IPAddress.TryParse(cfg.Endpoint, out ip))
            {
                ProbeResult dns = ProbeDns(cfg.Endpoint, ct);
                if (!dns.Ok || !IPAddress.TryParse(dns.Detail, out ip))
                {
                    return new ProbeResult
                    {
                        Ok = false,
                        LatencyMs = -1,
                        Target = cfg.Endpoint,
                        Detail = "DNS do servidor falhou: " + dns.Detail
                    };
                }
            }

            int pingMs = Math.Max(500, Math.Min(timeoutMs, 1500));
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    PingReply reply = ping.Send(ip, pingMs);
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        return new ProbeResult
                        {
                            Ok = true,
                            LatencyMs = Math.Max(1, (int)reply.RoundtripTime),
                            Target = cfg.Endpoint,
                            Detail = ip + " (ICMP)"
                        };
                    }
                }
            }
            catch { }

            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = cfg.Endpoint, Detail = "Cancelado" };

            // Many WireGuard servers filter ICMP: a fast TCP RST (ConnectionRefused) still proves network reachability
            SocketError err;
            var sw = Stopwatch.StartNew();
            ProbeResult tcp = ProbeTcpIp(ip, 443, Math.Max(500, Math.Min(timeoutMs, 1500)), ct, out err);
            sw.Stop();
            if (tcp.Ok || err == SocketError.ConnectionRefused)
            {
                int ms = tcp.Ok ? tcp.LatencyMs : Math.Max(1, (int)sw.ElapsedMilliseconds);
                return new ProbeResult
                {
                    Ok = true,
                    LatencyMs = ms,
                    Target = cfg.Endpoint,
                    Detail = ip + " (alcançável; ICMP filtrado)"
                };
            }

            return new ProbeResult
            {
                Ok = false,
                LatencyMs = -1,
                Target = cfg.Endpoint,
                Detail = "Sem resposta de " + ip + " (" + tcp.Detail + ")"
            };
        }

        public static List<AuditItem> AuditConfig(ConfigInfo cfg, Settings s, bool hasGhostV6, bool activeTunnel, bool appsNeedReconnect)
        {
            var items = new List<AuditItem>();
            if (cfg == null || !cfg.Readable)
            {
                items.Add(new AuditItem
                {
                    Key = "config",
                    Severity = AuditSeverity.Error,
                    Title = "Configuração indisponível",
                    Detail = "Selecione ou importe um servidor WireGuard válido."
                });
                return items;
            }

            if (cfg.PersistentKeepalive <= 0)
            {
                items.Add(new AuditItem
                {
                    Key = "keepalive",
                    Severity = AuditSeverity.Warn,
                    Title = "Sem PersistentKeepalive no arquivo",
                    Detail = "O VPN Panel injeta Keepalive = 25s automaticamente ao conectar para evitar quedas em repouso."
                });
            }
            else
            {
                items.Add(new AuditItem
                {
                    Key = "keepalive",
                    Severity = AuditSeverity.Ok,
                    Title = "PersistentKeepalive = " + cfg.PersistentKeepalive + "s",
                    Detail = "Mantém o túnel ativo atrás de NAT/roteador."
                });
            }

            if (cfg.Mtu == 0)
            {
                bool opt = s == null || s.OptimizeMtu;
                items.Add(new AuditItem
                {
                    Key = "mtu",
                    Severity = opt ? AuditSeverity.Ok : AuditSeverity.Warn,
                    Title = opt ? "MTU otimizado (1380)" : "MTU padrão não otimizado",
                    Detail = opt
                        ? "Será aplicado MTU = 1380 ao conectar para evitar travamentos."
                        : "Ative 'Otimizar MTU' nas Opções se páginas travarem em Wi-Fi/4G."
                });
            }
            else if (cfg.Mtu > 1420)
            {
                items.Add(new AuditItem
                {
                    Key = "mtu",
                    Severity = AuditSeverity.Warn,
                    Title = "MTU alto (" + cfg.Mtu + ")",
                    Detail = "Valores acima de 1420 podem causar fragmentação ou travamento em PPPoE/4G."
                });
            }
            else
            {
                items.Add(new AuditItem
                {
                    Key = "mtu",
                    Severity = AuditSeverity.Ok,
                    Title = "MTU = " + cfg.Mtu,
                    Detail = "Tamanho de pacote adequado para WireGuard."
                });
            }

            if (cfg.Mode != AppMode.All)
            {
                items.Add(new AuditItem
                {
                    Key = "lan",
                    Severity = AuditSeverity.Ok,
                    Title = cfg.HasDisallowedIps ? "DisallowedIPs (LAN) definido" : "Proteção de rede local automática",
                    Detail = cfg.HasDisallowedIps
                        ? "Sub-redes locais excluídas do túnel."
                        : "LAN, loopback e multicast são excluídos em runtime ao conectar."
                });
            }

            if (s != null && s.NetworkLock && cfg.Mode != AppMode.All)
            {
                items.Add(new AuditItem
                {
                    Key = "lock_split",
                    Severity = AuditSeverity.Warn,
                    Title = "Kill Switch ignorado no modo Split",
                    Detail = "Em 'Só estes' ou 'Exceto', o Kill Switch é desativado para não bloquear o DNS local."
                });
            }

            if (hasGhostV6)
            {
                items.Add(new AuditItem
                {
                    Key = "ghost_v6",
                    Severity = AuditSeverity.Error,
                    Title = "IPv6/rota fantasma detectado",
                    Detail = "Resto de sessão anterior na placa de rede. Use 'Reparar internet agora'."
                });
            }

            if (!activeTunnel && Tunnel.HasPendingNetworkLock)
            {
                items.Add(new AuditItem
                {
                    Key = "lock_flag",
                    Severity = AuditSeverity.Error,
                    Title = "Bloqueio residual do Kill Switch",
                    Detail = "Sessão anterior encerrou sem liberar o WFP. Use 'Reparar internet agora'."
                });
            }

            if (activeTunnel && appsNeedReconnect)
            {
                items.Add(new AuditItem
                {
                    Key = "apps_pending",
                    Severity = AuditSeverity.Warn,
                    Title = "Regras de apps pendentes",
                    Detail = "Você alterou os apps; reconecte para aplicar no túnel atual."
                });
            }

            return items;
        }

        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        [DllImport("iphlpapi.dll")]
        static extern int GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

        [DllImport("iphlpapi.dll")]
        static extern int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_IPINTERFACE_ROW
        {
            public ushort Family;
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public uint MaxReassemblySize;
            public ulong InterfaceIdentifier;
            public uint MinRouterAdvertisementInterval;
            public uint MaxRouterAdvertisementInterval;
            public byte AdvertisingEnabled;
            public byte ForwardingEnabled;
            public byte WeakHostSend;
            public byte WeakHostReceive;
            public byte UseAutomaticMetric;
            public byte UseNeighborUnreachabilityDetection;
            public byte ManagedAddressConfigurationSupported;
            public byte OtherStatefulConfigurationSupported;
            public byte AdvertiseDefaultRoute;
            public int RouterDiscoveryBehavior;
            public uint DadTransmits;
            public uint BaseReachableTime;
            public uint RetransmitTime;
            public uint PathMtuDiscoveryTimeout;
            public int LinkLocalAddressBehavior;
            public uint LinkLocalAddressTimeout;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public uint[] ZoneIndices;
            public uint SitePrefixLength;
            public uint Metric;
            public uint NlMtu;
            public byte Connected;
            public byte SupportsWakeUpPatterns;
            public byte SupportsNeighborDiscovery;
            public byte SupportsRouterDiscovery;
            public uint ReachableTime;
            public byte TransmitOffload;
            public byte ReceiveOffload;
            public byte DisableDefaultRoutes;
        }

        static bool TryGetIpv4InterfaceMetric(int ifIdx, out uint metric)
        {
            metric = 0;
            if (ifIdx <= 0) return false;
            try
            {
                var row = new MIB_IPINTERFACE_ROW
                {
                    Family = 2, // AF_INET
                    InterfaceIndex = (uint)ifIdx,
                    ZoneIndices = new uint[16]
                };
                if (GetIpInterfaceEntry(ref row) == 0)
                {
                    metric = row.Metric;
                    return true;
                }
            }
            catch { }
            return false;
        }

        static bool IsVirtualOrTunnelAdapter(NetworkInterface ni)
        {
            if (ni == null) return false;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                return true;
            // the panel's own Rede de amigos adapter: never a physical card, never a competing VPN
            if (Lan.IsLanAdapter(ni)) return true;
            string d = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
            string[] tokens = new[]
            {
                "wiresock", "wintun", "wireguard", "tap-windows", "openvpn", "tailscale",
                "proton", "cloudflare", "warp", "mullvad", "exitlag", "radmin", "hamachi",
                "zerotier", "hyper-v", "vethernet", "virtualbox", "vmware", "bluetooth", "npcap"
            };
            for (int i = 0; i < tokens.Length; i++)
                if (d.Contains(tokens[i])) return true;
            return false;
        }

        static int dnsQueryId;
        static readonly byte[] DnsQueryTail = new byte[]
        {
            0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, (byte)'o', (byte)'n', (byte)'e',
            0x03, (byte)'o', (byte)'n', (byte)'e',
            0x03, (byte)'o', (byte)'n', (byte)'e',
            0x03, (byte)'o', (byte)'n', (byte)'e',
            0x00,
            0x00, 0x01, // QTYPE = A
            0x00, 0x01  // QCLASS = IN
        };

        public static ProbeResult ProbeUdpDns(string serverIp, int port, int timeoutMs, CancellationToken ct)
        {
            string host = string.IsNullOrEmpty(serverIp) ? "1.1.1.1" : serverIp.Trim();
            int targetPort = port > 0 ? port : 53;
            string targetLabel = host + ":" + targetPort;
            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = "Cancelado" };

            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                ProbeResult dns = ProbeDns(host, ct);
                if (!dns.Ok || !IPAddress.TryParse(dns.Detail, out ip))
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = "DNS falhou: " + dns.Detail };
            }

            int timeout = Math.Max(200, timeoutMs);
            int idNum = Interlocked.Increment(ref dnsQueryId) & 0xFFFF;
            if (idNum == 0) idNum = 1;
            byte idHi = (byte)((idNum >> 8) & 0xFF);
            byte idLo = (byte)(idNum & 0xFF);
            byte[] pkt = new byte[2 + DnsQueryTail.Length];
            pkt[0] = idHi;
            pkt[1] = idLo;
            Buffer.BlockCopy(DnsQueryTail, 0, pkt, 2, DnsQueryTail.Length);

            var sw = Stopwatch.StartNew();
            Socket sock = null;
            try
            {
                sock = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                sock.SendTimeout = timeout;
                sock.ReceiveTimeout = timeout;
                using (ct.Register(() => { try { if (sock != null) sock.Close(); } catch { } }))
                {
                    sock.Connect(new IPEndPoint(ip, targetPort));
                    sock.Send(pkt);
                    byte[] resp = new byte[512];
                    while (true)
                    {
                        int n = sock.Receive(resp);
                        if (n >= 12 && resp[0] == idHi && resp[1] == idLo && (resp[2] & 0x80) != 0)
                        {
                            sw.Stop();
                            int rcode = resp[3] & 0x0F;
                            if (rcode == 0)
                            {
                                return new ProbeResult
                                {
                                    Ok = true,
                                    LatencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds),
                                    Target = targetLabel,
                                    Detail = "UDP DNS OK"
                                };
                            }
                            return new ProbeResult
                            {
                                Ok = false,
                                LatencyMs = -1,
                                Target = targetLabel,
                                Detail = "DNS RCODE " + rcode
                            };
                        }
                        if (sw.ElapsedMilliseconds >= timeout)
                            return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = "Tempo esgotado" };
                    }
                }
            }
            catch (SocketException sex)
            {
                if (ct.IsCancellationRequested)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = "Cancelado" };
                string msg = sex.SocketErrorCode == SocketError.TimedOut ? "Tempo esgotado" : ShortErr(sex.Message);
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = msg };
            }
            catch (ObjectDisposedException)
            {
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = ct.IsCancellationRequested ? "Cancelado" : "Socket fechado" };
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = "Cancelado" };
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = targetLabel, Detail = ShortErr(ex.Message) };
            }
            finally
            {
                if (sock != null) { try { sock.Close(); } catch { } }
            }
        }

        public static IPAddress GetPhysicalGateway()
        {
            try
            {
                uint bestIdx = 0;
                GetBestInterface(0x01010101, out bestIdx);
                IPAddress bestGw = null;
                IPAddress lowestMetricGw = null;
                uint lowestMetric = uint.MaxValue;

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (IsVirtualOrTunnelAdapter(ni)) continue;
                    IPInterfaceProperties props;
                    IPv4InterfaceProperties v4;
                    try
                    {
                        props = ni.GetIPProperties();
                        v4 = props.GetIPv4Properties();
                        if (v4 == null) continue;
                    }
                    catch { continue; }

                    IPAddress gwAddr = null;
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(gw.Address))
                        {
                            gwAddr = gw.Address;
                            break;
                        }
                    }
                    if (gwAddr == null) continue;
                    if (bestIdx != 0 && (uint)v4.Index == bestIdx)
                    {
                        bestGw = gwAddr;
                        break;
                    }
                    uint m;
                    if (!TryGetIpv4InterfaceMetric(v4.Index, out m)) m = 999;
                    if (m < lowestMetric)
                    {
                        lowestMetric = m;
                        lowestMetricGw = gwAddr;
                    }
                }
                return bestGw ?? lowestMetricGw;
            }
            catch { return null; }
        }

        // Fast check for whether the host has an active physical uplink with a valid gateway
        // (excludes virtual/VPN adapters so auto-reconnect never burns attempts during offline flaps).
        public static bool HasPhysicalUplink()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable()) return false;
                if (GetPhysicalGateway() != null) return true;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (IsVirtualOrTunnelAdapter(ni)) continue;
                    try
                    {
                        foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                        {
                            if (gw == null || gw.Address == null) continue;
                            if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(gw.Address))
                                return true;
                            if (gw.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                !gw.Address.IsIPv6LinkLocal && !IPAddress.IPv6None.Equals(gw.Address))
                                return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        public static bool IsPhysicalAdapter(NetworkInterface ni)
        {
            return ni != null && ni.OperationalStatus == OperationalStatus.Up && !IsVirtualOrTunnelAdapter(ni);
        }

        public static bool HasExternalActiveVpnAdapter()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.Name != null && ni.Name.Equals(Tunnel.WintunTunnelName, StringComparison.OrdinalIgnoreCase)) continue;
                    string combined = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
                    if (combined.Contains("wiresock") || combined.Contains("wg_tunnel")) continue;
                    if (combined.Contains("tailscale") || combined.Contains("wireguard") || combined.Contains("wintun") ||
                        combined.Contains("openvpn") || combined.Contains("tap-windows") || combined.Contains("proton") ||
                        combined.Contains("cloudflare") || combined.Contains("mullvad") || combined.Contains("exitlag") ||
                        combined.Contains("zerotier") || combined.Contains("radmin") || combined.Contains("hamachi"))
                        return true;
                }
            }
            catch { }
            return false;
        }

        public class MtuResult
        {
            public int MaxPayload;
            public int EstimatedMtu;
            public bool Ok;
            public string Detail;
        }

        public static int RecommendSafeWireGuardMtu(int maxPayload, bool ipv6Endpoint)
        {
            if (maxPayload <= 0) return 0;
            if (maxPayload >= 1472 && !ipv6Endpoint) return 1380;
            int overhead = ipv6Endpoint ? 80 : 60;
            int physMtu = maxPayload + 28;
            int safe = (physMtu - overhead) & ~3;
            if (safe < 1280) safe = 1280;
            if (safe > 1380) safe = 1380;
            return safe;
        }

        public static bool RuntimeHasProbeApp(string cfgName)
        {
            if (string.IsNullOrEmpty(cfgName)) return false;
            try
            {
                string p = Path.Combine(Paths.Runtime, cfgName + ".conf");
                if (!File.Exists(p)) return false;
                string text = File.ReadAllText(p);
                return text.IndexOf(ConfFile.ProbeProcessName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static ProbeResult ParseProbeHelperLine(string line, string host, int port, bool fallbackCancel)
        {
            string target = host + ":" + port;
            if (string.IsNullOrEmpty(line))
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = fallbackCancel ? "Cancelado" : "Sem resposta da sonda" };
            string t = line.Trim();
            if (t.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
            {
                int ms;
                if (!int.TryParse(t.Substring(3).Trim(), out ms) || ms < 1) ms = 1;
                return new ProbeResult { Ok = true, LatencyMs = ms, Target = target, Detail = "UDP túnel OK" };
            }
            if (t.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase))
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = ShortErr(t.Substring(4)) };
            return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = ShortErr(t) };
        }

        public static ProbeResult ProbeTunnelUdpViaHelper(string host, int port, int timeoutMs, CancellationToken ct)
        {
            string target = (host ?? "1.1.1.1") + ":" + (port > 0 ? port : 53);
            if (ct.IsCancellationRequested)
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Cancelado" };
            string probeExe = ConfFile.EnsureProbeBinary();
            if (string.IsNullOrEmpty(probeExe))
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = "Sonda indisponível" };

            int timeout = Math.Max(300, timeoutMs);
            var psi = new ProcessStartInfo(probeExe,
                string.Format("--probe-udp {0} {1} {2}", host ?? "1.1.1.1", port > 0 ? port : 53, timeout))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    Process localP = p;
                    using (var killer = new System.Threading.Timer(_ => { try { if (!localP.HasExited) localP.Kill(); } catch { } },
                        null, timeout + 1500, Timeout.Infinite))
                    using (ct.Register(() => { try { if (!localP.HasExited) localP.Kill(); } catch { } }))
                    {
                        string line = p.StandardOutput.ReadLine();
                        if (!p.WaitForExit(timeout + 1200))
                        {
                            try { p.Kill(); } catch { }
                        }
                        return ParseProbeHelperLine(line, host ?? "1.1.1.1", port > 0 ? port : 53, ct.IsCancellationRequested);
                    }
                }
            }
            catch (Exception ex)
            {
                return new ProbeResult { Ok = false, LatencyMs = -1, Target = target, Detail = ShortErr(ex.Message) };
            }
        }

        public sealed class TunnelUdpSession : IDisposable
        {
            Process proc;
            readonly object ioLock = new object();

            public TunnelUdpSession()
            {
                string probeExe = ConfFile.EnsureProbeBinary();
                if (string.IsNullOrEmpty(probeExe)) return;
                var psi = new ProcessStartInfo(probeExe, "--probe-udp-loop")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                try { proc = Process.Start(psi); }
                catch { proc = null; }
            }

            public bool IsReady { get { return proc != null && !proc.HasExited; } }

            public ProbeResult Query(string host, int port, int timeoutMs, CancellationToken ct)
            {
                string h = string.IsNullOrEmpty(host) ? "1.1.1.1" : host;
                int pt = port > 0 ? port : 53;
                lock (ioLock)
                {
                    if (ct.IsCancellationRequested)
                        return new ProbeResult { Ok = false, LatencyMs = -1, Target = h + ":" + pt, Detail = "Cancelado" };
                    Process pSnap = proc;
                    if (!IsReady || pSnap == null)
                        return new ProbeResult { Ok = false, LatencyMs = -1, Target = h + ":" + pt, Detail = "Sonda encerrada" };
                    try
                    {
                        int waitLimit = Math.Max(400, timeoutMs) + 800;
                        bool timedOut = false;
                        using (var killer = new System.Threading.Timer(_ =>
                            {
                                timedOut = true;
                                try { if (!pSnap.HasExited) pSnap.Kill(); } catch { }
                            }, null, waitLimit, Timeout.Infinite))
                        using (ct.Register(() => { try { if (!pSnap.HasExited) pSnap.Kill(); } catch { } }))
                        {
                            pSnap.StandardInput.WriteLine(h + " " + pt + " " + Math.Max(200, timeoutMs));
                            pSnap.StandardInput.Flush();
                            string line = pSnap.StandardOutput.ReadLine();
                            if (timedOut || line == null)
                            {
                                proc = null;
                                try { pSnap.Dispose(); } catch { }
                                return new ProbeResult { Ok = false, LatencyMs = -1, Target = h + ":" + pt, Detail = timedOut ? "Tempo esgotado na sonda" : "Sonda encerrada" };
                            }
                            return ParseProbeHelperLine(line, h, pt, ct.IsCancellationRequested);
                        }
                    }
                    catch (Exception ex)
                    {
                        proc = null;
                        try { pSnap.Dispose(); } catch { }
                        return new ProbeResult { Ok = false, LatencyMs = -1, Target = h + ":" + pt, Detail = ShortErr(ex.Message) };
                    }
                }
            }

            public void Dispose()
            {
                Process p = proc;
                proc = null;
                if (p == null) return;
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.StandardInput.WriteLine("QUIT"); p.StandardInput.Flush(); } catch { }
                        if (!p.WaitForExit(400))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        public static MtuResult ProbeMtu(IPAddress target, CancellationToken ct)
        {
            return ProbeMtu(target, 1200, false, ct);
        }

        public static MtuResult ProbeMtu(IPAddress target, int timeoutMs, CancellationToken ct)
        {
            return ProbeMtu(target, timeoutMs, false, ct);
        }

        public static MtuResult ProbeMtu(IPAddress target, int timeoutMs, bool quickPreCheck, CancellationToken ct)
        {
            IPAddress dst = target ?? IPAddress.Parse("1.1.1.1");
            if (ct.IsCancellationRequested)
                return new MtuResult { MaxPayload = 0, EstimatedMtu = 0, Ok = false, Detail = "Cancelado" };

            int sendTimeout = Math.Max(400, timeoutMs);
            int[] payloads = new[] { 1472, 1392, 1352, 1312, 1252 };
            var opts = new PingOptions(64, true);
            bool sawPacketTooBig = false;
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    for (int i = 0; i < payloads.Length; i++)
                    {
                        if (ct.IsCancellationRequested)
                            return new MtuResult { MaxPayload = 0, EstimatedMtu = 0, Ok = false, Detail = "Cancelado" };
                        int size = payloads[i];
                        byte[] buf = new byte[size];
                        PingReply rep = ping.Send(dst, sendTimeout, buf, opts);
                        // Avoid miscalibrating down to 1360 on a single stray packet loss at 1472
                        if (i == 0 && (rep == null || (rep.Status == IPStatus.TimedOut)) && !ct.IsCancellationRequested)
                            rep = ping.Send(dst, sendTimeout, buf, opts);
                        if (rep != null && rep.Status == IPStatus.Success)
                        {
                            int mtu = size + 28;
                            string detail = size == 1472
                                ? "MTU 1500 OK (sem fragmentação até 1472 B)"
                                : "Limite em " + size + " B (MTU ~" + mtu + "; 1472 B fragmentou)";
                            return new MtuResult { MaxPayload = size, EstimatedMtu = mtu, Ok = true, Detail = detail };
                        }
                        if (rep != null && rep.Status == IPStatus.PacketTooBig) sawPacketTooBig = true;
                        // In fast pre-connect check, if both 1472 and 1392 timed out with no PacketTooBig, ICMP is blocked
                        if (quickPreCheck && i >= 1 && !sawPacketTooBig)
                        {
                            return new MtuResult
                            {
                                MaxPayload = 0,
                                EstimatedMtu = 0,
                                Ok = false,
                                Detail = "ICMP bloqueado ou sem resposta em " + dst
                            };
                        }
                    }

                    if (ct.IsCancellationRequested)
                        return new MtuResult { MaxPayload = 0, EstimatedMtu = 0, Ok = false, Detail = "Cancelado" };
                    PingReply ctrl = ping.Send(dst, 900, new byte[32], new PingOptions(64, false));
                    if (ctrl != null && ctrl.Status == IPStatus.Success)
                    {
                        return new MtuResult
                        {
                            MaxPayload = 0,
                            EstimatedMtu = 0,
                            Ok = false,
                            Detail = sawPacketTooBig
                                ? "Fragmentação severa: pacotes <= 1252 B excedem o MTU (" + dst + ")"
                                : "Fragmentação ou bloqueio DF: 32 B passou, 1252 B falhou (" + dst + ")"
                        };
                    }
                    return new MtuResult
                    {
                        MaxPayload = 0,
                        EstimatedMtu = 0,
                        Ok = false,
                        Detail = "ICMP bloqueado ou sem resposta em " + dst
                    };
                }
            }
            catch (Exception ex)
            {
                return new MtuResult { MaxPayload = 0, EstimatedMtu = 0, Ok = false, Detail = ShortErr(ex.Message) };
            }
        }

        public class ConflictReport
        {
            public bool WireSockServiceRunning;
            public int WireSockProcessCount;
            public bool NdisDriverPresent;
            public bool WintunActive;
            public bool ExitLagUiRunning;
            public bool ExitLagGameOverlap;
            public List<string> OverlappingGames = new List<string>();
            public List<string> ConflictingAdapters = new List<string>();
            public List<string> LowMetricAdapters = new List<string>();
            public List<string> DualPhysicalAdapters = new List<string>();
            public List<string> ConflictingServices = new List<string>();
            public List<string> PassiveAdapters = new List<string>();
            public List<string> PassiveServices = new List<string>();
            public List<string> PassiveNotes = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        public static HashSet<int> GetIpv4DefaultRouteIndices()
        {
            var set = new HashSet<int>();
            const int ErrorInsufficientBuffer = 122;
            int size = 0;
            try
            {
                int rc = GetIpForwardTable(IntPtr.Zero, ref size, false);
                if (rc != ErrorInsufficientBuffer && rc != 0) return set;
                for (int attempt = 0; attempt < 3 && size > 0; attempt++)
                {
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        rc = GetIpForwardTable(buf, ref size, false);
                        if (rc == 0)
                        {
                            if (size >= 4)
                            {
                                int count = Marshal.ReadInt32(buf, 0);
                                int maxRows = (size - 4) / 56;
                                if (count > maxRows) count = maxRows;
                                for (int i = 0; i < count; i++)
                                {
                                    int off = 4 + i * 56;
                                    uint dest = unchecked((uint)Marshal.ReadInt32(buf, off));
                                    uint mask = unchecked((uint)Marshal.ReadInt32(buf, off + 4));
                                    int ifIdx = Marshal.ReadInt32(buf, off + 16);
                                    // Match 0.0.0.0/0 OR WireGuard/Tailscale split default 0.0.0.0/1 & 128.0.0.0/1
                                    // (in x86/x64 little-endian memory, 128.0.0.0 in network byte order is 0x00000080)
                                    bool isDefault = (dest == 0u && mask == 0u) ||
                                                     (dest == 0u && mask == 0x00000080u) ||
                                                     (dest == 0x00000080u && mask == 0x00000080u);
                                    if (isDefault && ifIdx > 0) set.Add(ifIdx);
                                }
                            }
                            return set;
                        }
                        if (rc != ErrorInsufficientBuffer) return set;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch { }
            return set;
        }

        public static bool HasActiveDefaultGateway(IPInterfaceProperties props, int v4IfIndex, HashSet<int> v4DefaultRoutes, out bool isApipaOnly)
        {
            isApipaOnly = false;
            bool hasGw = v4IfIndex > 0 && v4DefaultRoutes != null && v4DefaultRoutes.Contains(v4IfIndex);
            if (props == null) return hasGw;
            if (!hasGw)
            {
                try
                {
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                    {
                        if (gw == null || gw.Address == null) continue;
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            if (!IPAddress.Any.Equals(gw.Address)) { hasGw = true; break; }
                        }
                        else if (gw.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (!gw.Address.IsIPv6LinkLocal && !IPAddress.IPv6None.Equals(gw.Address)) { hasGw = true; break; }
                        }
                    }
                }
                catch { }
            }

            try
            {
                bool anyV4 = false;
                bool allApipa = true;
                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    if (ua == null || ua.Address == null || ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    anyV4 = true;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (!(b.Length == 4 && b[0] == 169 && b[1] == 254))
                    {
                        allApipa = false;
                        break;
                    }
                }
                isApipaOnly = anyV4 && allApipa;
            }
            catch { }

            return hasGw;
        }

        static readonly string[] KnownGameProcesses = new[]
        {
            "RobloxPlayerBeta", "RobloxPlayerLauncher", "VALORANT", "VALORANT-Win64-Shipping",
            "cs2", "FortniteClient-Win64-Shipping", "League of Legends", "LeagueClientUx",
            "Overwatch", "GenshinImpact", "Minecraft.Windows"
        };

        public static ConflictReport DetectConflicts()
        {
            return DetectConflicts(AppMode.All, null);
        }

        public static ConflictReport DetectConflicts(AppMode effMode, IList<string> effApps)
        {
            var r = new ConflictReport();
            bool tunnelActive = Tunnel.Active(true);
            r.WintunActive = tunnelActive && Tunnel.ActiveEngine == TunnelEngine.Wintun;
            try { r.WireSockServiceRunning = Paths.WireSockServiceRunning; } catch { }
            try
            {
                Process[] procs = Tunnel.Find();
                r.WireSockProcessCount = procs.Length;
                for (int i = 0; i < procs.Length; i++) { try { procs[i].Dispose(); } catch { } }
            }
            catch { }

            try
            {
                System.ServiceProcess.ServiceController[] devs = System.ServiceProcess.ServiceController.GetDevices();
                try
                {
                    foreach (var d in devs)
                    {
                        try
                        {
                            if (d.ServiceName.Equals("ndiswgc", StringComparison.OrdinalIgnoreCase) &&
                                d.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                            {
                                r.NdisDriverPresent = true;
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    foreach (var d in devs) { try { d.Dispose(); } catch { } }
                }
            }
            catch { }

            bool exitLagUiRunning = false;
            try
            {
                Process[] elProcs = Process.GetProcessesByName("ExitLag");
                exitLagUiRunning = elProcs.Length > 0;
                for (int i = 0; i < elProcs.Length; i++) { try { elProcs[i].Dispose(); } catch { } }
            }
            catch { }
            r.ExitLagUiRunning = exitLagUiRunning;

            if (exitLagUiRunning && tunnelActive)
            {
                if (effMode == AppMode.Only && effApps != null)
                {
                    for (int i = 0; i < effApps.Count; i++)
                    {
                        string app = (effApps[i] ?? "").Trim();
                        string clean = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app.Substring(0, app.Length - 4) : app;
                        for (int j = 0; j < KnownGameProcesses.Length; j++)
                        {
                            if (clean.Equals(KnownGameProcesses[j], StringComparison.OrdinalIgnoreCase) &&
                                !r.OverlappingGames.Contains(app))
                            {
                                r.OverlappingGames.Add(app);
                            }
                        }
                    }
                    if (r.OverlappingGames.Count > 0) r.ExitLagGameOverlap = true;
                }
                else if (effMode == AppMode.All)
                {
                    try
                    {
                        for (int i = 0; i < KnownGameProcesses.Length; i++)
                        {
                            Process[] gp = Process.GetProcessesByName(KnownGameProcesses[i]);
                            bool running = gp.Length > 0;
                            for (int j = 0; j < gp.Length; j++) { try { gp[j].Dispose(); } catch { } }
                            if (running && !r.OverlappingGames.Contains(KnownGameProcesses[i]))
                                r.OverlappingGames.Add(KnownGameProcesses[i]);
                        }
                    }
                    catch { }
                    r.ExitLagGameOverlap = true;
                }
            }

            // Scan adapters first so services (e.g., Tailscale) know whether a default route / Exit Node is active
            HashSet<int> v4DefaultRoutes = GetIpv4DefaultRouteIndices();
            string[] vpnAdapterTokens = new[]
            {
                "wintun", "wireguard", "tap-windows", "openvpn", "tailscale",
                "proton", "cloudflare", "cloudflarewarp", "cloudflare warp", "mullvad",
                "exitlag", "radmin", "hamachi", "zerotier"
            };
            bool tailscaleDefaultGw = false;
            try
            {
                var upPhysical = new List<string>();
                var upPhysicalAllMetrics = new List<string>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (ni.Name.Equals(Tunnel.WintunTunnelName, StringComparison.OrdinalIgnoreCase)) continue;
                        string combined = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
                        uint metric = 0;
                        bool hasMetric = false;
                        IPInterfaceProperties props = null;
                        bool isApipaOnly = false;
                        bool hasDefaultGw = false;
                        try
                        {
                            props = ni.GetIPProperties();
                            if (props != null)
                            {
                                int v4Idx = 0;
                                IPv4InterfaceProperties v4 = props.GetIPv4Properties();
                                if (v4 != null)
                                {
                                    v4Idx = v4.Index;
                                    hasMetric = TryGetIpv4InterfaceMetric(v4.Index, out metric);
                                }
                                hasDefaultGw = HasActiveDefaultGateway(props, v4Idx, v4DefaultRoutes, out isApipaOnly);
                            }
                        }
                        catch { }

                        bool isWsAdapter = combined.Contains("wiresock");
                        bool isOtherVpn = false;
                        for (int i = 0; i < vpnAdapterTokens.Length; i++)
                        {
                            if (combined.Contains(vpnAdapterTokens[i])) { isOtherVpn = true; break; }
                        }

                        if (isWsAdapter && !tunnelActive)
                        {
                            string info = hasMetric ? ni.Name + " (métrica " + metric + ")" : ni.Name;
                            if (!r.ConflictingAdapters.Contains(info)) r.ConflictingAdapters.Add(info);
                            if (hasMetric && metric <= 15 && !r.LowMetricAdapters.Contains(info))
                                r.LowMetricAdapters.Add(info);
                        }
                        else if (isOtherVpn)
                        {
                            bool isOverlay = combined.Contains("tailscale") || combined.Contains("radmin") ||
                                             combined.Contains("hamachi") || combined.Contains("zerotier") ||
                                             combined.Contains("exitlag");
                            if (combined.Contains("tailscale") && hasDefaultGw) tailscaleDefaultGw = true;

                            if (!hasDefaultGw && (isOverlay || isApipaOnly))
                            {
                                string tag = isApipaOnly ? "em espera · APIPA" : "sem Exit Node / gateway";
                                string info = ni.Name + " (" + tag + ")";
                                if (!r.PassiveAdapters.Contains(info)) r.PassiveAdapters.Add(info);
                            }
                            else
                            {
                                string info = hasMetric ? ni.Name + " (métrica " + metric + ")" : ni.Name;
                                if (!r.ConflictingAdapters.Contains(info)) r.ConflictingAdapters.Add(info);
                                if (hasMetric && metric <= 15 && !r.LowMetricAdapters.Contains(info))
                                    r.LowMetricAdapters.Add(info);
                            }
                        }
                        else if (!IsVirtualOrTunnelAdapter(ni))
                        {
                            string info = hasMetric ? ni.Name + " (métrica " + metric + ")" : ni.Name;
                            if (hasDefaultGw)
                            {
                                upPhysicalAllMetrics.Add(info);
                                if (hasMetric && metric <= 15) upPhysical.Add(info);
                            }
                        }
                    }
                    catch { }
                }
                if (upPhysical.Count > 1)
                {
                    for (int i = 0; i < upPhysical.Count; i++)
                        if (!r.LowMetricAdapters.Contains(upPhysical[i])) r.LowMetricAdapters.Add(upPhysical[i]);
                }
                if (upPhysicalAllMetrics.Count > 1)
                {
                    for (int i = 0; i < upPhysicalAllMetrics.Count; i++)
                        if (!r.DualPhysicalAdapters.Contains(upPhysicalAllMetrics[i])) r.DualPhysicalAdapters.Add(upPhysicalAllMetrics[i]);
                }
            }
            catch { }

            string[] badSvcTokens = new[]
            {
                "tailscale", "wireguard", "cloudflarewarp", "cloudflare warp", "cloudflare", "warp-svc",
                "masque", "protonvpn", "openvpn", "mullvad", "exitlag", "radmin", "hamachi",
                "zerotier", "netlimiter", "killer network", "killernetwork", "killerservice"
            };
            try
            {
                System.ServiceProcess.ServiceController[] svcs = System.ServiceProcess.ServiceController.GetServices();
                try
                {
                    foreach (var s in svcs)
                    {
                        try
                        {
                            if (s.Status != System.ServiceProcess.ServiceControllerStatus.Running) continue;
                            if (s.ServiceName.StartsWith("wiresock-", StringComparison.OrdinalIgnoreCase)) continue;
                            if (r.WintunActive && s.ServiceName.Equals(Tunnel.WintunServiceName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (s.ServiceName.Equals("warpjitsvc", StringComparison.OrdinalIgnoreCase)) continue;
                            string low = (s.ServiceName + " " + s.DisplayName).ToLowerInvariant();
                            for (int i = 0; i < badSvcTokens.Length; i++)
                            {
                                if (low.Contains(badSvcTokens[i]))
                                {
                                    string label = string.IsNullOrEmpty(s.DisplayName) ? s.ServiceName : s.DisplayName;
                                    if (s.ServiceName.Equals("ExitLagPmService", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (exitLagUiRunning)
                                        {
                                            string warnMsg = r.ExitLagGameOverlap
                                                ? (r.OverlappingGames.Count > 0
                                                    ? "Sobreposição VPN + ExitLag em " + string.Join(", ", r.OverlappingGames.ToArray()) + " (use o reparo para isolar o jogo)."
                                                    : "ExitLag ativo junto do modo Todos (isole o jogo em Split ou feche um dos dois).")
                                                : "ExitLag em execução (evite otimizar o mesmo jogo na VPN e no ExitLag ao mesmo tempo).";
                                            if (!r.Warnings.Contains(warnMsg)) r.Warnings.Add(warnMsg);
                                        }
                                        else
                                        {
                                            string tag = "ExitLagPmService (em espera)";
                                            if (!r.PassiveServices.Contains(tag)) r.PassiveServices.Add(tag);
                                        }
                                    }
                                    else if (low.Contains("tailscale") && !tailscaleDefaultGw)
                                    {
                                        string tag = label + " (sem Exit Node)";
                                        if (!r.PassiveServices.Contains(tag)) r.PassiveServices.Add(tag);
                                    }
                                    else
                                    {
                                        if (!r.ConflictingServices.Contains(label)) r.ConflictingServices.Add(label);
                                    }
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    foreach (var s in svcs) { try { s.Dispose(); } catch { } }
                }
            }
            catch { }

            if (r.WireSockServiceRunning)
                r.Warnings.Add("Serviço nativo do aplicativo oficial do WireSock em execução (conflito direto de driver).");
            if (r.WireSockProcessCount > 1)
                r.Warnings.Add("Múltiplos processos wiresock-client.exe rodando ao mesmo tempo (" + r.WireSockProcessCount + ").");
            if (!r.NdisDriverPresent && !r.WintunActive)
                r.Warnings.Add("Driver NDIS do WireSock (ndiswgc) não está ativo no Windows.");
            if (r.ConflictingAdapters.Count > 0)
                r.Warnings.Add("Adaptador VPN/virtual concorrente ativo: " + string.Join(", ", r.ConflictingAdapters.ToArray()) + ".");
            if (r.LowMetricAdapters.Count > 0)
                r.Warnings.Add("Métrica de rota <= 15 concorrente (pode confundir o roteamento do WireSock): " + string.Join(", ", r.LowMetricAdapters.ToArray()) + ".");
            if (r.DualPhysicalAdapters.Count > 1)
                r.Warnings.Add("Múltiplas conexões físicas ativas ao mesmo tempo (" + string.Join(" + ", r.DualPhysicalAdapters.ToArray()) + ") — oscilações no Wi-Fi reiniciam o filtro do WireSock durante jogos.");
            if (r.ConflictingServices.Count > 0)
                r.Warnings.Add("Serviço VPN/rede concorrente ativo: " + string.Join(", ", r.ConflictingServices.ToArray()) + ".");

            if (r.PassiveAdapters.Count > 0 || r.PassiveServices.Count > 0)
            {
                var passiveList = new List<string>(r.PassiveServices);
                for (int i = 0; i < r.PassiveAdapters.Count; i++)
                {
                    if (!passiveList.Contains(r.PassiveAdapters[i])) passiveList.Add(r.PassiveAdapters[i]);
                }
                r.PassiveNotes.Add("Em espera (compatível sem Exit Node / uso simultâneo): " + string.Join(", ", passiveList.ToArray()) + ".");
            }

            return r;
        }
    }
}
