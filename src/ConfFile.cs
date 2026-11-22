// VPN Panel - ConfigInfo / ConfFile: .conf parsing, editing and the runtime copies handed to the tunnel engines.

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
    // What the UI shows about one config
    class ConfigInfo
    {
        static readonly Dictionary<string, ConfigInfo> cache = new Dictionary<string, ConfigInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex CountryHintRegex = new Regex(@"(?:^|[^A-Za-z])([A-Za-z]{2})-(?:([A-Za-z]{2})#|(?:[A-Za-z]{3}-wg-)|FREE|[0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string Name;
        public string Server;    // first comment in [Peer], e.g. "# US-FREE#40" in Proton configs
        public string Endpoint;  // host only
        public int EndpointPort; // UDP port in [Peer] Endpoint, or 0
        public int Mtu;          // MTU in [Interface], or 0 when not specified
        public bool AutoMtu;     // true when MTU = 1380 was added by VPN Panel (# vpn-panel-mtu)
        public int PersistentKeepalive; // PersistentKeepalive in [Peer], or 0 when absent/0
        public bool HasDisallowedIps;   // true when DisallowedIPs is defined in [Peer]
        public string GuessedCountry; // provisional ISO country code from server comment/name
        public AppMode Mode;
        public List<string> Apps = new List<string>();
        public bool Readable;

        static string GuessCountry(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Match m = CountryHintRegex.Match(text);
            if (!m.Success) return null;
            string code = (m.Groups[2].Success && m.Groups[2].Value.Length == 2 ? m.Groups[2].Value : m.Groups[1].Value).ToUpperInvariant();
            if (code == "UK") code = "GB";
            try
            {
                var r = new RegionInfo(code);
                return r.TwoLetterISORegionName.ToUpperInvariant();
            }
            catch { return null; }
        }

        static readonly object cacheLock = new object();

        // Every write by the app invalidates its entry, and the configs watcher clears the cache for
        // changes made outside the app, so a cached entry is trusted without touching the disk.
        public static void Invalidate(string name)
        {
            if (name != null) lock (cacheLock) { cache.Remove(name); }
            Paths.ConfigsChanged();
        }

        public static void InvalidateAll()
        {
            lock (cacheLock) { cache.Clear(); }
            Paths.ConfigsChanged();
        }

        public static ConfigInfo Read(string name)
        {
            ConfigInfo cached;
            lock (cacheLock)
            {
                if (cache.TryGetValue(name, out cached)) return cached;
            }

            string path = Paths.Conf(name);
            var c = new ConfigInfo { Name = name };
            try
            {
                string[] lines = File.ReadAllLines(path);
                bool inPeer = false, inInterface = false, prevMtuMarker = false;
                foreach (string line in lines)
                {
                    Match m = ConfFile.SectionLine.Match(line);
                    if (m.Success)
                    {
                        string sec = m.Groups[1].Value;
                        inPeer = sec.Equals("Peer", StringComparison.OrdinalIgnoreCase);
                        inInterface = sec.Equals("Interface", StringComparison.OrdinalIgnoreCase);
                        prevMtuMarker = false;
                        continue;
                    }
                    if (inInterface && c.Mtu == 0)
                    {
                        if (line.Trim().Equals(ConfFile.MtuMarker, StringComparison.OrdinalIgnoreCase))
                        {
                            prevMtuMarker = true;
                            continue;
                        }
                        m = ConfFile.MtuLine.Match(line);
                        if (m.Success)
                        {
                            int parsedMtu;
                            c.Mtu = int.TryParse(m.Groups[1].Value, out parsedMtu) && parsedMtu > 0 ? parsedMtu : -1;
                            c.AutoMtu = prevMtuMarker || line.IndexOf(ConfFile.MtuMarker, StringComparison.OrdinalIgnoreCase) >= 0;
                            prevMtuMarker = false;
                            continue;
                        }
                        if (line.Trim().Length > 0) prevMtuMarker = false;
                    }
                    if (!inPeer) continue;
                    if (c.Server == null)
                    {
                        m = ConfFile.CommentLine.Match(line);
                        if (m.Success && m.Groups[1].Value.Length <= 40 && !m.Groups[1].Value.Contains(" the "))
                            c.Server = m.Groups[1].Value;
                    }
                    if (c.Endpoint == null)
                    {
                        m = ConfFile.EndpointLine.Match(line);
                        if (m.Success)
                        {
                            string ep = m.Groups[1].Value;
                            int colon = ep.StartsWith("[") ? ep.LastIndexOf("]:") : ep.LastIndexOf(':');
                            if (ep.StartsWith("["))
                            {
                                int close = ep.IndexOf(']');
                                c.Endpoint = ep.Substring(1, Math.Max(0, close - 1));
                                if (colon > close)
                                {
                                    int port;
                                    if (int.TryParse(ep.Substring(colon + 2), out port) && port > 0) c.EndpointPort = port;
                                }
                            }
                            else if (colon >= 0)
                            {
                                c.Endpoint = ep.Substring(0, colon);
                                int port;
                                if (int.TryParse(ep.Substring(colon + 1), out port) && port > 0) c.EndpointPort = port;
                            }
                            else
                            {
                                c.Endpoint = ep;
                            }
                        }
                    }
                    if (c.PersistentKeepalive == 0)
                    {
                        m = ConfFile.KeepaliveLine.Match(line);
                        if (m.Success)
                        {
                            int ka;
                            if (int.TryParse(m.Groups[1].Value, out ka) && ka > 0) c.PersistentKeepalive = ka;
                        }
                    }
                    if (!c.HasDisallowedIps && ConfFile.DisallowedIpsLine.IsMatch(line))
                        c.HasDisallowedIps = true;
                }
                c.GuessedCountry = GuessCountry(c.Server) ?? GuessCountry(c.Name);
                c.Mode = ConfFile.ReadApps(lines, c.Apps);
                c.Readable = true;
                lock (cacheLock) { cache[name] = c; }
            }
            catch (FileNotFoundException) { lock (cacheLock) { cache[name] = c; } }
            catch (DirectoryNotFoundException) { lock (cacheLock) { cache[name] = c; } }
            catch { }
            return c;
        }

        public string Detail
        {
            get
            {
                if (!Readable) return "Não foi possível ler o arquivo";
                var parts = new[] { Server, Endpoint }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
                return parts.Length > 0 ? string.Join(" · ", parts) : "Servidor WireGuard";
            }
        }

        // Where the server is, from the cached lookup of its endpoint (null until known)
        public Geo.Place Place { get { return Endpoint != null ? Geo.Cached(Endpoint) : null; } }

        // Country code for flags/badges: uses ipinfo.io result when available, or provisional hint from config name/comment
        public string CountryCode
        {
            get
            {
                Geo.Place p = Place;
                if (p != null && !string.IsNullOrEmpty(p.Country)) return p.Country;
                return GuessedCountry;
            }
        }
    }

    // Config files: import/rename/delete, and the split tunneling keys (AllowedApps / DisallowedApps)
    // in the [Peer] section, leaving every other line of the file untouched.
    static class ConfFile
    {
        public const string MtuMarker = "# vpn-panel-mtu";
        public static readonly Regex SectionLine = new Regex(@"^\s*\[(\w+)\]\s*$", RegexOptions.Compiled);
        public static readonly Regex CommentLine = new Regex(@"^\s*#\s*(.+?)\s*$", RegexOptions.Compiled);
        public static readonly Regex EndpointLine = new Regex(@"^\s*Endpoint\s*=\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex MtuLine = new Regex(@"^\s*MTU\s*=\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AddressLine = new Regex(@"^\s*Address\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DnsLine = new Regex(@"^\s*DNS\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AppsLine = new Regex(@"^\s*(AllowedApps|DisallowedApps)\s*=\s*([^#]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AllowedIpsLine = new Regex(@"^\s*AllowedIPs\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex DisallowedIpsLine = new Regex(@"^\s*DisallowedIPs\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex KeepaliveLine = new Regex(@"^\s*PersistentKeepalive\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex V6KeyLine = new Regex(@"^(\s*(?:Address|DNS|AllowedIPs)\s*=\s*)([^#]*)(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [DllImport("dnsapi.dll")]
        static extern int DnsFlushResolverCache();

        // Strips IPv6 entries from Address/DNS/AllowedIPs. Returns null when the whole line was IPv6-only
        // so callers omit that line completely instead of leaving IPv6 active.
        public static string StripV6List(Match m, out bool changed)
        {
            changed = false;
            string prefix = m.Groups[1].Value;
            string raw = m.Groups[2].Value;
            string comment = m.Groups[3].Success ? " " + m.Groups[3].Value.Trim() : "";
            var v4 = new List<string>();
            bool hadV6 = false;
            foreach (string part in raw.Split(','))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;
                if (item.Contains(':')) hadV6 = true;
                else v4.Add(item);
            }
            if (!hadV6) return m.Value;
            changed = true;
            if (v4.Count == 0) return null;
            return prefix + string.Join(", ", v4) + comment;
        }

        static int CountMaskBits(byte b)
        {
            int c = 0;
            while (b != 0) { c += b & 1; b >>= 1; }
            return c;
        }

        static bool IsInSubnet(IPAddress addr, byte[] net, byte[] mask)
        {
            if (addr == null || addr.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] a = addr.GetAddressBytes();
            for (int i = 0; i < 4; i++)
                if ((a[i] & mask[i]) != net[i]) return false;
            return true;
        }

        // Builds DisallowedIPs for split-tunneling so loopback, link-local, multicast/mDNS (5353), broadcast,
        // and the local LAN subnet (e.g. 192.168.x.0/24, Steam Remote Play/LAN discovery) never enter the tunnel.
        static string BuildDisallowedIps(string tunnelIpv4)
        {
            var list = new List<string> { "127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32" };
            IPAddress tunAddr = null;
            if (!string.IsNullOrEmpty(tunnelIpv4)) IPAddress.TryParse(tunnelIpv4, out tunAddr);
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        byte[] net = new byte[4];
                        int prefix = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            net[i] = (byte)(ip[i] & mask[i]);
                            prefix += CountMaskBits(mask[i]);
                        }
                        if (prefix < 8 || prefix >= 32) continue;
                        if (tunAddr != null && IsInSubnet(tunAddr, net, mask)) continue;
                        string cidr = new IPAddress(net) + "/" + prefix;
                        if (!list.Contains(cidr)) list.Add(cidr);
                    }
                }
            }
            catch { }
            return string.Join(", ", list);
        }

        public const string ProbeProcessName = "VpnPanelProbe";
        public static string ProbeBinaryPath
        {
            get
            {
                string nextToExe = Path.Combine(Paths.ExeDir, ProbeProcessName + ".exe");
                if (File.Exists(nextToExe)) return nextToExe;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VPN Panel", ProbeProcessName + ".exe");
            }
        }

        static bool FilesIdenticalSha256(string pathA, string pathB)
        {
            if (!File.Exists(pathA) || !File.Exists(pathB)) return false;
            var aInfo = new FileInfo(pathA);
            var bInfo = new FileInfo(pathB);
            if (aInfo.Length != bInfo.Length) return false;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fa = File.OpenRead(pathA))
            using (var fb = File.OpenRead(pathB))
            {
                byte[] ha = sha.ComputeHash(fa);
                byte[] hb = sha.ComputeHash(fb);
                if (ha.Length != hb.Length) return false;
                for (int i = 0; i < ha.Length; i++) if (ha[i] != hb[i]) return false;
                return true;
            }
        }

        static bool SyncCopyIfNeeded(string src, string dst, bool checkHash)
        {
            try
            {
                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sInfo = new FileInfo(src);
                var dInfo = new FileInfo(dst);
                if (dInfo.Exists && dInfo.Length == sInfo.Length && dInfo.LastWriteTimeUtc == sInfo.LastWriteTimeUtc)
                {
                    if (!checkHash || FilesIdenticalSha256(src, dst)) return true;
                }
                File.Copy(src, dst, true);
                try { File.SetLastWriteTimeUtc(dst, sInfo.LastWriteTimeUtc); } catch { }
                return checkHash ? FilesIdenticalSha256(src, dst) : File.Exists(dst);
            }
            catch { return false; }
        }

        public static string EnsureProbeBinary()
        {
            try
            {
                string src = Paths.Exe;
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return null;
                string besideExe = Path.Combine(Paths.ExeDir, ProbeProcessName + ".exe");
                bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                if (SyncCopyIfNeeded(src, besideExe, false)) return besideExe;
                if (File.Exists(besideExe)) return besideExe;
                // Only fall back to LocalAppData when running non-elevated (e.g. dev tests); elevated always uses ExeDir
                if (!isAdmin)
                {
                    string localFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VPN Panel", ProbeProcessName + ".exe");
                    if (SyncCopyIfNeeded(src, localFallback, true)) return localFallback;
                }
                return null;
            }
            catch { return null; }
        }

        // PreUp/PostUp/PreDown/PostDown are left out on purpose: they are commands, and an imported .conf
        // must never get to run one through the WireGuard service (it only obeys them when a registry
        // switch is on, but there is no reason to hand them over).
        static readonly HashSet<string> StandardWgInterfaceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PrivateKey", "Address", "ListenPort", "MTU", "DNS", "Table"
        };

        static readonly HashSet<string> StandardWgPeerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs", "PersistentKeepalive"
        };

        static readonly HashSet<string> WintunIncompatibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Socks5Proxy", "Socks5ProxyUsername", "Socks5ProxyPassword", "BypassLanTraffic",
            "Jc", "Jmin", "Jmax", "S1", "S2", "H1", "H2", "H3", "H4"
        };

        static string ExtractIniKey(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string t = line.TrimStart();
            if (t.StartsWith("#") || t.StartsWith(";") || t.StartsWith("[")) return null;
            int eq = t.IndexOf('=');
            return eq > 0 ? t.Substring(0, eq).Trim() : null;
        }

        // Returns false when a config relies on WireSock/AmneziaWG extensions or explicit DisallowedIPs
        // that standard official WireGuard (Wintun) does not support cleanly.
        public static bool IsWintunCompatible(string name)
        {
            ConfigInfo info = ConfigInfo.Read(name);
            if (info == null || !info.Readable) return false;
            try
            {
                foreach (string line in File.ReadAllLines(Paths.Conf(name)))
                {
                    string key = ExtractIniKey(line);
                    if (key == null) continue;
                    if (WintunIncompatibleKeys.Contains(key) || DisallowedIpsLine.IsMatch(line))
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        // True when the active/target Wintun config routes all IPv4 internet (0.0.0.0/0 or 0.0.0.0/1+128.0.0.0/1),
        // making a 1.1.1.1:53 UDP probe valid as a tunnel handshake fallback.
        public static bool WintunRoutesInternet(string configName)
        {
            try
            {
                string path = File.Exists(Paths.WintunRuntimeConf) ? Paths.WintunRuntimeConf
                            : (!string.IsNullOrEmpty(configName) ? Paths.Conf(configName) : null);
                if (path == null || !File.Exists(path)) return false;
                bool hasDefault = false, hasLow = false, hasHigh = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    string key = ExtractIniKey(line);
                    if (!"AllowedIPs".Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    int cmt = line.IndexOf('#');
                    if (eq < 0) continue;
                    string raw = (cmt > eq ? line.Substring(eq + 1, cmt - eq - 1) : line.Substring(eq + 1));
                    foreach (string part in raw.Split(','))
                    {
                        string cidr = part.Trim();
                        if (cidr.Equals("0.0.0.0/0", StringComparison.OrdinalIgnoreCase)) hasDefault = true;
                        else if (cidr.Equals("0.0.0.0/1", StringComparison.OrdinalIgnoreCase)) hasLow = true;
                        else if (cidr.Equals("128.0.0.0/1", StringComparison.OrdinalIgnoreCase)) hasHigh = true;
                    }
                }
                return hasDefault || (hasLow && hasHigh);
            }
            catch { return false; }
        }

        static string RewriteWintunAllowedIpsNoLock(string line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) return line;
            int cmt = line.IndexOf('#');
            string prefix = line.Substring(0, eq + 1) + " ";
            string raw = cmt >= 0 ? line.Substring(eq + 1, cmt - eq - 1) : line.Substring(eq + 1);
            string tail = cmt >= 0 ? " " + line.Substring(cmt).TrimStart() : "";
            var outParts = new List<string>();
            foreach (string part in raw.Split(','))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;
                if (item.Equals("0.0.0.0/0", StringComparison.OrdinalIgnoreCase))
                {
                    outParts.Add("0.0.0.0/1");
                    outParts.Add("128.0.0.0/1");
                }
                else if (item.Equals("::/0", StringComparison.OrdinalIgnoreCase))
                {
                    outParts.Add("::/1");
                    outParts.Add("8000::/1");
                }
                else
                {
                    outParts.Add(item);
                }
            }
            return prefix + string.Join(", ", outParts.ToArray()) + tail;
        }

        // Normalizes the config into Paths.Runtime before launching WireSock:
        // - All modes: ensures PersistentKeepalive (25s or custom e.g. 15s for GameStabilityMode) in [Peer]
        //   and strips legacy inline "# vpn-panel-mtu" comments from MTU lines.
        // - Custom DNS (when set): overrides/inserts [Interface] DNS = <customDns> cleanly in both All and Split modes.
        // - Split-tunneling modes (Only / Except):
        //   1. Strips IPv6 addresses/routes so WireSock does not bind IPv6/::0 onto the physical NIC.
        //   2. Without CustomDns, comments out [Interface] DNS = ... so Windows Dnscache stays on local DNS.
        //   3. Ensures DisallowedIPs excludes loopback/LAN/multicast from the tunnel.
        //   4. In Only mode, appends VpnPanelProbe to runtime AllowedApps so diagnostics can test inside the tunnel.
        public static string EffectivePath(string name, out bool ipv6Stripped)
        {
            bool dummy;
            return EffectivePath(name, 0, 0, null, false, true, out ipv6Stripped, out dummy);
        }

        public static string EffectivePath(string name, int maxMtu, out bool ipv6Stripped)
        {
            bool dummy;
            return EffectivePath(name, maxMtu, 0, null, false, true, out ipv6Stripped, out dummy);
        }

        public static string EffectivePath(string name, int maxMtu, int keepaliveSec, string customDns, out bool ipv6Stripped)
        {
            bool dummy;
            return EffectivePath(name, maxMtu, keepaliveSec, customDns, false, true, out ipv6Stripped, out dummy);
        }

        public static string EffectivePath(string name, int maxMtu, int keepaliveSec, string customDns, out bool ipv6Stripped, out bool probeInjected)
        {
            return EffectivePath(name, maxMtu, keepaliveSec, customDns, false, true, out ipv6Stripped, out probeInjected);
        }

        public static string WintunPath(string name, int maxMtu, int keepaliveSec, string customDns)
        {
            return WintunPath(name, maxMtu, keepaliveSec, customDns, true);
        }

        public static string WintunPath(string name, int maxMtu, int keepaliveSec, string customDns, bool networkLock)
        {
            bool dummyV6, dummyProbe;
            return EffectivePath(name, maxMtu, keepaliveSec, customDns, true, networkLock, out dummyV6, out dummyProbe);
        }

        static string EffectivePath(string name, int maxMtu, int keepaliveSec, string customDns, bool forWintun, bool wintunNetworkLock, out bool ipv6Stripped, out bool probeInjected)
        {
            ipv6Stripped = false;
            probeInjected = false;
            if (maxMtu > 0 && maxMtu < 1200) maxMtu = 1200;
            int targetKeepalive = keepaliveSec > 0 ? keepaliveSec : 25;
            string cleanDns = "";
            if (!string.IsNullOrEmpty(customDns))
            {
                IPAddress parsedDns;
                if (IPAddress.TryParse(customDns.Trim(), out parsedDns) && parsedDns.AddressFamily == AddressFamily.InterNetwork)
                    cleanDns = parsedDns.ToString();
            }
            string src = Paths.Conf(name);
            ConfigInfo info = ConfigInfo.Read(name);
            if (!info.Readable)
            {
                if (forWintun) throw new InvalidDataException("Configuração ilegível para Wintun.");
                return src;
            }
            bool splitMode = !forWintun && info.Mode != AppMode.All;
            bool needProbeApp = !forWintun && info.Mode == AppMode.Only && !string.IsNullOrEmpty(EnsureProbeBinary());
            try
            {
                string[] rawLines = File.ReadAllLines(src);
                var lines = new List<string>(rawLines.Length + 4);
                bool anyChanged = false, inInterface = false, inPeer = false;
                bool hasDisallowedIps = false, hasKeepalive = false, keepaliveInserted = false, mtuHandled = false, dnsHandled = false;
                int peerCount = 0;
                string tunnelIpv4 = null;

                foreach (string rawLine in rawLines)
                {
                    Match sec = SectionLine.Match(rawLine);
                    if (sec.Success)
                    {
                        inInterface = IsInterface(sec);
                        inPeer = IsPeer(sec);
                        continue;
                    }
                    if (inPeer && DisallowedIpsLine.IsMatch(rawLine)) hasDisallowedIps = true;
                    if (inPeer)
                    {
                        Match km = KeepaliveLine.Match(rawLine);
                        if (km.Success)
                        {
                            int ka;
                            if (int.TryParse(km.Groups[1].Value, out ka) && ka > 0) hasKeepalive = true;
                        }
                    }
                    if (inInterface && AddressLine.IsMatch(rawLine))
                    {
                        Match am = V6KeyLine.Match(rawLine);
                        if (am.Success)
                        {
                            foreach (string p in am.Groups[2].Value.Split(','))
                            {
                                string item = p.Trim();
                                if (item.Length > 0 && !item.Contains(':'))
                                {
                                    int slash = item.IndexOf('/');
                                    tunnelIpv4 = slash > 0 ? item.Substring(0, slash).Trim() : item;
                                    break;
                                }
                            }
                        }
                    }
                }

                inInterface = false;
                inPeer = false;
                for (int i = 0; i < rawLines.Length; i++)
                {
                    string line = rawLines[i];
                    Match sec = SectionLine.Match(line);
                    if (sec.Success)
                    {
                        if (inInterface && cleanDns.Length > 0 && !dnsHandled)
                        {
                            lines.Add("DNS = " + cleanDns);
                            dnsHandled = true;
                            anyChanged = true;
                        }
                        if (inInterface && maxMtu > 0 && !mtuHandled)
                        {
                            lines.Add("MTU = " + maxMtu);
                            mtuHandled = true;
                            anyChanged = true;
                        }
                        if (inPeer && peerCount == 1 && !hasKeepalive && !keepaliveInserted)
                        {
                            lines.Add("PersistentKeepalive = " + targetKeepalive);
                            keepaliveInserted = true;
                            anyChanged = true;
                        }
                        inInterface = IsInterface(sec);
                        inPeer = IsPeer(sec);
                        if (inPeer) peerCount++;
                        lines.Add(line);
                        continue;
                    }
                    if (inInterface && MtuLine.IsMatch(line))
                    {
                        if (line.IndexOf(MtuMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int idx = line.IndexOf('#');
                            if (idx > 0)
                            {
                                line = line.Substring(0, idx).TrimEnd();
                                anyChanged = true;
                            }
                        }
                        mtuHandled = true;
                        if (maxMtu > 0)
                        {
                            Match mtuM = MtuLine.Match(line);
                            int curMtu;
                            if (!mtuM.Success || !int.TryParse(mtuM.Groups[1].Value, out curMtu) || curMtu <= 0 || curMtu > maxMtu)
                            {
                                line = "MTU = " + maxMtu;
                                anyChanged = true;
                            }
                        }
                    }
                    if (forWintun)
                    {
                        string iniKey = ExtractIniKey(line);
                        if (iniKey != null)
                        {
                            bool allowed = (inInterface && StandardWgInterfaceKeys.Contains(iniKey)) ||
                                           (inPeer && StandardWgPeerKeys.Contains(iniKey));
                            if (!allowed)
                            {
                                anyChanged = true;
                                continue; // strip all WireSock/non-standard keys across every section and peer
                            }
                            if (!wintunNetworkLock && inPeer && AllowedIpsLine.IsMatch(line))
                            {
                                string rewritten = RewriteWintunAllowedIpsNoLock(line);
                                if (!rewritten.Equals(line, StringComparison.Ordinal)) anyChanged = true;
                                line = rewritten;
                            }
                        }
                    }
                    if (!forWintun && inPeer && (AppsLine.IsMatch(line) || DisallowedIpsLine.IsMatch(line)))
                    {
                        if (needProbeApp && peerCount == 1)
                        {
                            Match am = AppsLine.Match(line);
                            if (am.Success && am.Groups[1].Value.Equals("AllowedApps", StringComparison.OrdinalIgnoreCase))
                            {
                                string rawList = am.Groups[2].Value ?? "";
                                bool already = false;
                                foreach (string part in rawList.Split(','))
                                {
                                    string pt = part.Trim();
                                    if (pt.Equals(ProbeProcessName, StringComparison.OrdinalIgnoreCase) ||
                                        pt.Equals(ProbeBinaryPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        already = true;
                                        probeInjected = true;
                                        break;
                                    }
                                }
                                if (!already)
                                {
                                    int cmt = line.IndexOf('#');
                                    string head = (cmt >= 0 ? line.Substring(0, cmt) : line).TrimEnd();
                                    string tail = cmt >= 0 ? " " + line.Substring(cmt).TrimStart() : "";
                                    line = head + (head.EndsWith("=") ? " " : ", ") + ProbeProcessName + tail;
                                    probeInjected = true;
                                    anyChanged = true;
                                }
                            }
                        }
                    }
                    if (inPeer && peerCount == 1)
                    {
                        Match km = KeepaliveLine.Match(line);
                        if (km.Success)
                        {
                            int ka;
                            if (!int.TryParse(km.Groups[1].Value, out ka) || ka <= 0 || (keepaliveSec > 0 && ka > keepaliveSec))
                            {
                                line = "PersistentKeepalive = " + targetKeepalive;
                                anyChanged = true;
                            }
                            keepaliveInserted = true;
                        }
                    }
                    if (inInterface && DnsLine.IsMatch(line))
                    {
                        if (cleanDns.Length > 0)
                        {
                            if (!dnsHandled)
                            {
                                string wanted = "DNS = " + cleanDns;
                                if (!line.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) anyChanged = true;
                                lines.Add(wanted);
                                dnsHandled = true;
                            }
                            else
                            {
                                anyChanged = true;
                            }
                            continue;
                        }
                        else if (splitMode)
                        {
                            lines.Add("# " + line.Trim() + " # vpn-panel: DNS local mantido em split tunneling");
                            anyChanged = true;
                            continue;
                        }
                    }
                    if (splitMode)
                    {
                        Match m = V6KeyLine.Match(line);
                        if (m.Success)
                        {
                            bool lineChanged;
                            line = StripV6List(m, out lineChanged);
                            if (lineChanged) anyChanged = true;
                            if (line == null) continue;
                        }
                    }
                    lines.Add(line);
                    if (splitMode && inPeer && !hasDisallowedIps && AllowedIpsLine.IsMatch(line))
                    {
                        lines.Add("DisallowedIPs = " + BuildDisallowedIps(tunnelIpv4));
                        hasDisallowedIps = true;
                        anyChanged = true;
                    }
                }
                if (inInterface && cleanDns.Length > 0 && !dnsHandled)
                {
                    lines.Add("DNS = " + cleanDns);
                    dnsHandled = true;
                    anyChanged = true;
                }
                if (inInterface && maxMtu > 0 && !mtuHandled)
                {
                    lines.Add("MTU = " + maxMtu);
                    mtuHandled = true;
                    anyChanged = true;
                }
                if (inPeer && peerCount == 1 && !hasKeepalive && !keepaliveInserted)
                {
                    lines.Add("PersistentKeepalive = " + targetKeepalive);
                    anyChanged = true;
                }
                if (splitMode && inPeer && !hasDisallowedIps)
                {
                    lines.Add("DisallowedIPs = " + BuildDisallowedIps(tunnelIpv4));
                    anyChanged = true;
                }
                if (forWintun)
                {
                    Directory.CreateDirectory(Paths.WintunRuntimeDir);
                    File.WriteAllLines(Paths.WintunRuntimeConf, lines.ToArray(), new UTF8Encoding(false));
                    return Paths.WintunRuntimeConf;
                }
                if (!splitMode && !anyChanged) return src;
                Directory.CreateDirectory(Paths.Runtime);
                string dst = Path.Combine(Paths.Runtime, name + ".conf");
                File.WriteAllLines(dst, lines.ToArray(), new UTF8Encoding(false));
                ipv6Stripped = splitMode;
                return dst;
            }
            catch
            {
                if (forWintun) throw;
                return src;
            }
        }

        static string Prefix64(IPAddress addr)
        {
            if (addr == null || addr.AddressFamily != AddressFamily.InterNetworkV6) return null;
            if (addr.IsIPv6LinkLocal || IPAddress.IsLoopback(addr)) return null;
            byte[] b = addr.GetAddressBytes();
            return string.Format("{0:x2}{1:x2}:{2:x2}{3:x2}:{4:x2}{5:x2}:{6:x2}{7:x2}",
                b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]);
        }

        static bool IsWireSockAdapter(NetworkInterface ni)
        {
            if (ni == null) return false;
            string d = (ni.Description ?? "") + " " + (ni.Name ?? "");
            return d.IndexOf("WireSock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   d.IndexOf("wg_tunnel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void CollectVpnIpv6(out HashSet<string> addrs, out HashSet<string> gws, out HashSet<string> prefixes)
        {
            addrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            gws = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            try
            {
                if (Directory.Exists(Paths.Configs)) files.AddRange(Directory.GetFiles(Paths.Configs, "*.conf"));
                if (Directory.Exists(Paths.Runtime)) files.AddRange(Directory.GetFiles(Paths.Runtime, "*.conf"));
            }
            catch { }
            foreach (string f in files)
            {
                try
                {
                    foreach (string line in File.ReadAllLines(f))
                    {
                        bool isAddr = AddressLine.IsMatch(line);
                        bool isDns = !isAddr && DnsLine.IsMatch(line);
                        if (!isAddr && !isDns) continue;
                        Match m = V6KeyLine.Match(line);
                        if (!m.Success) continue;
                        foreach (string part in m.Groups[2].Value.Split(','))
                        {
                            string item = part.Trim();
                            if (!item.Contains(':')) continue;
                            int slash = item.IndexOf('/');
                            if (slash > 0) item = item.Substring(0, slash).Trim();
                            IPAddress parsed;
                            if (!IPAddress.TryParse(item, out parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6) continue;
                            string norm = parsed.ToString();
                            string p64 = Prefix64(parsed);
                            if (p64 != null) prefixes.Add(p64);
                            if (isAddr)
                            {
                                addrs.Add(norm);
                                if (norm.EndsWith(":2", StringComparison.OrdinalIgnoreCase))
                                    gws.Add(norm.Substring(0, norm.Length - 2) + ":1");
                                byte[] b = parsed.GetAddressBytes();
                                byte[] gw1 = new byte[16];
                                Array.Copy(b, gw1, 8);
                                gw1[15] = 1;
                                gws.Add(new IPAddress(gw1).ToString());
                            }
                            else gws.Add(norm);
                        }
                    }
                }
                catch { }
            }
        }

        // True if any physical adapter still holds a ghost IPv6 address or route left behind by an earlier WireSock session
        public static bool HasGhostIpv6()
        {
            try
            {
                bool tunnelUp = Tunnel.Active(true);
                if (tunnelUp && !Tunnel.Ipv6Isolated) return false;
                HashSet<string> addrs, gws, prefixes;
                CollectVpnIpv6(out addrs, out gws, out prefixes);
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        bool isWs = IsWireSockAdapter(ni);
                        if (tunnelUp && isWs) continue;
                        if (!tunnelUp && isWs && ni.OperationalStatus == OperationalStatus.Up)
                            return true;
                        IPInterfaceProperties props = ni.GetIPProperties();
                        foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                            if (addrs.Contains(ua.Address.ToString())) return true;
                            string p64 = Prefix64(ua.Address);
                            if (p64 != null && prefixes.Contains(p64)) return true;
                        }
                        foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                        {
                            if (gw.Address == null || gw.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                            if (gws.Contains(gw.Address.ToString())) return true;
                            string p64 = Prefix64(gw.Address);
                            if (p64 != null && prefixes.Contains(p64)) return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        static readonly Regex DefaultV6RouteLine = new Regex(@"(?:^|\s)::/0\s+(\d+)\s+(\S+)", RegexOptions.Compiled);

        public static void FlushDnsCache()
        {
            try { DnsFlushResolverCache(); } catch { }
        }

        // Purges any lingering VPN IPv6 addresses/routes stuck on physical NICs (from v2.6 or full-tunnel runs)
        // and flushes the Windows DNS resolver cache only when tainted state is purged or forceFlush is requested.
        public static void CleanGhostNetwork()
        {
            CleanGhostNetwork(false);
        }

        public static void CleanGhostNetwork(bool forceFlush)
        {
            if (forceFlush) FlushDnsCache();
            try
            {
                HashSet<string> addrs, gws, prefixes;
                CollectVpnIpv6(out addrs, out gws, out prefixes);
                var netshCmds = new List<string>();
                var taintedIfs = new HashSet<int>();
                var wireSockIfs = new HashSet<int>();
                bool tunnelUp = Tunnel.Active(true);

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    bool isWsAdapter = IsWireSockAdapter(ni);
                    if (tunnelUp && isWsAdapter) continue;
                    IPInterfaceProperties props;
                    int ifIdx;
                    try
                    {
                        props = ni.GetIPProperties();
                        IPv6InterfaceProperties v6Props = props.GetIPv6Properties();
                        if (v6Props == null) continue;
                        ifIdx = v6Props.Index;
                    }
                    catch { continue; }

                    bool isWs = !tunnelUp && isWsAdapter;
                    if (isWs) wireSockIfs.Add(ifIdx);

                    bool foundOnNic = false;
                    var nicVpnAddrs = new List<string>();
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        string s = ua.Address.ToString();
                        string p64 = Prefix64(ua.Address);
                        if (addrs.Contains(s) || (p64 != null && prefixes.Contains(p64)))
                        {
                            foundOnNic = true;
                            nicVpnAddrs.Add(s);
                        }
                    }
                    var nicVpnGws = new List<string>();
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                    {
                        if (gw.Address == null || gw.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        string s = gw.Address.ToString();
                        string p64 = Prefix64(gw.Address);
                        if (gws.Contains(s) || (p64 != null && prefixes.Contains(p64)))
                        {
                            foundOnNic = true;
                            nicVpnGws.Add(s);
                        }
                    }
                    if (!foundOnNic && !isWs) continue;
                    taintedIfs.Add(ifIdx);

                    foreach (string gw in gws)
                    {
                        netshCmds.Add("interface ipv6 delete route ::/0 interface=" + ifIdx + " " + gw + " store=active");
                        netshCmds.Add("interface ipv6 delete route ::/0 interface=" + ifIdx + " " + gw + " store=persistent");
                    }
                    foreach (string gw in nicVpnGws)
                    {
                        if (!gws.Contains(gw))
                        {
                            netshCmds.Add("interface ipv6 delete route ::/0 interface=" + ifIdx + " " + gw + " store=active");
                            netshCmds.Add("interface ipv6 delete route ::/0 interface=" + ifIdx + " " + gw + " store=persistent");
                        }
                    }
                    foreach (string addr in addrs)
                    {
                        netshCmds.Add("interface ipv6 delete address interface=" + ifIdx + " addr=" + addr + " store=active");
                        netshCmds.Add("interface ipv6 delete address interface=" + ifIdx + " addr=" + addr + " store=persistent");
                    }
                    foreach (string addr in nicVpnAddrs)
                    {
                        if (!addrs.Contains(addr))
                        {
                            netshCmds.Add("interface ipv6 delete address interface=" + ifIdx + " addr=" + addr + " store=active");
                            netshCmds.Add("interface ipv6 delete address interface=" + ifIdx + " addr=" + addr + " store=persistent");
                        }
                    }
                }

                // Only query netsh route table when a NIC was tainted, a ghost WireSock adapter appeared, or explicit reset was requested
                if (forceFlush || taintedIfs.Count > 0 || wireSockIfs.Count > 0)
                {
                    try
                    {
                        var showPsi = new ProcessStartInfo("netsh.exe", "interface ipv6 show route")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using (Process sp = Process.Start(showPsi))
                        {
                            string routeOut = sp.StandardOutput.ReadToEnd();
                            sp.WaitForExit(3000);
                            foreach (string rline in routeOut.Split('\n'))
                            {
                                Match rm = DefaultV6RouteLine.Match(rline);
                                if (!rm.Success) continue;
                                int rIfIdx;
                                if (!int.TryParse(rm.Groups[1].Value, out rIfIdx)) continue;
                                string nextHopRaw = rm.Groups[2].Value.Trim();
                                IPAddress nh;
                                bool isIp = IPAddress.TryParse(nextHopRaw, out nh) && nh.AddressFamily == AddressFamily.InterNetworkV6;
                                string nhNorm = isIp ? nh.ToString() : nextHopRaw;
                                string nhPrefix = isIp ? Prefix64(nh) : null;
                                bool match = wireSockIfs.Contains(rIfIdx) ||
                                             (isIp && (gws.Contains(nhNorm) || addrs.Contains(nhNorm) || (nhPrefix != null && prefixes.Contains(nhPrefix)))) ||
                                             (taintedIfs.Contains(rIfIdx) && (!isIp || !nh.IsIPv6LinkLocal));
                                if (match)
                                {
                                    string cmd = isIp
                                        ? "interface ipv6 delete route ::/0 interface=" + rIfIdx + " " + nhNorm
                                        : "interface ipv6 delete route ::/0 interface=" + rIfIdx;
                                    if (!netshCmds.Contains(cmd + " store=active")) netshCmds.Add(cmd + " store=active");
                                    if (!netshCmds.Contains(cmd + " store=persistent")) netshCmds.Add(cmd + " store=persistent");
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (netshCmds.Count > 0)
                {
                    netshCmds.Add("exit");
                    var psi = new ProcessStartInfo("netsh.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (Process p = Process.Start(psi))
                    {
                        foreach (string cmd in netshCmds) p.StandardInput.WriteLine(cmd);
                        p.StandardInput.Close();
                        p.WaitForExit(4000);
                    }
                    try { DnsFlushResolverCache(); } catch { }
                }
            }
            catch { }
        }

        public static void DeleteRuntimeCopies()
        {
            try
            {
                if (Directory.Exists(Paths.Runtime))
                    foreach (string f in Directory.GetFiles(Paths.Runtime, "*.conf"))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        static bool IsPeer(Match section)
        {
            return section.Groups[1].Value.Equals("Peer", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsInterface(Match section)
        {
            return section.Groups[1].Value.Equals("Interface", StringComparison.OrdinalIgnoreCase);
        }

        public static AppMode ReadApps(IEnumerable<string> lines, List<string> apps)
        {
            apps.Clear();
            var mode = AppMode.All;
            bool inPeer = false;
            foreach (string line in lines)
            {
                Match m = SectionLine.Match(line);
                if (m.Success) { inPeer = IsPeer(m); continue; }
                if (!inPeer) continue;
                m = AppsLine.Match(line);
                if (!m.Success) continue;
                bool allowed = m.Groups[1].Value.Equals("AllowedApps", StringComparison.OrdinalIgnoreCase);
                // WireSock evaluates AllowedApps first, so it wins when both are present
                if (mode == AppMode.Only && !allowed) continue;
                apps.Clear();
                apps.AddRange(m.Groups[2].Value.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                mode = allowed ? AppMode.Only : AppMode.Except;
            }
            return apps.Count == 0 ? AppMode.All : mode;
        }

        public static void WriteApps(string path, AppMode mode, IList<string> apps)
        {
            Rewrite(path, true, mode, apps, false, 0);
        }

        public static void Rewrite(string path, bool updateApps, AppMode mode, IList<string> apps, bool updateMtu, int mtu)
        {
            if (!updateApps && !updateMtu) return;
            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            int offset = bom ? 3 : 0, count = raw.Length - offset;
            Encoding enc = new UTF8Encoding(false, true);
            string text;
            try { text = enc.GetString(raw, offset, count); }
            catch (DecoderFallbackException)
            {
                enc = Encoding.Default;
                text = enc.GetString(raw, offset, count);
            }
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            bool trailingNewline = text.EndsWith("\n");
            List<string> lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (trailingNewline) lines.RemoveAt(lines.Count - 1);

            if (updateMtu)
            {
                int ifaces = 0, mtuInsertAt = -1;
                bool inIface = false, hasProviderMtu = false, nextMtuIsAuto = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    Match m = SectionLine.Match(lines[i]);
                    if (m.Success)
                    {
                        inIface = IsInterface(m);
                        nextMtuIsAuto = false;
                        if (inIface && ++ifaces == 1) mtuInsertAt = i + 1;
                        continue;
                    }
                    if (!inIface) continue;
                    if (lines[i].Trim().Equals(MtuMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        lines.RemoveAt(i--);
                        nextMtuIsAuto = true;
                        continue;
                    }
                    if (MtuLine.IsMatch(lines[i]))
                    {
                        if (nextMtuIsAuto || lines[i].IndexOf(MtuMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                            lines.RemoveAt(i--);
                        else
                            hasProviderMtu = true;
                        nextMtuIsAuto = false;
                        continue;
                    }
                    if (lines[i].Trim().Length > 0) nextMtuIsAuto = false;
                    if (ifaces == 1 && AddressLine.IsMatch(lines[i])) mtuInsertAt = i + 1;
                }
                if (mtu > 0 && !hasProviderMtu && mtuInsertAt >= 0)
                {
                    lines.Insert(mtuInsertAt, MtuMarker);
                    lines.Insert(mtuInsertAt + 1, "MTU = " + mtu);
                }
            }

            if (updateApps)
            {
                // Drop the existing keys and find where to insert the new one: after AllowedIPs in the first [Peer]
                int peers = 0, insertAt = -1;
                bool inPeer = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    Match m = SectionLine.Match(lines[i]);
                    if (m.Success)
                    {
                        inPeer = IsPeer(m);
                        if (inPeer && ++peers == 1) insertAt = i + 1;
                        continue;
                    }
                    if (!inPeer) continue;
                    if (AppsLine.IsMatch(lines[i])) { lines.RemoveAt(i--); continue; }
                    if (peers == 1 && AllowedIpsLine.IsMatch(lines[i])) insertAt = i + 1;
                }
                if (insertAt < 0) throw new InvalidDataException("Seção [Peer] não encontrada no arquivo.");
                if (mode != AppMode.All && apps != null && apps.Count > 0)
                    lines.Insert(insertAt, (mode == AppMode.Only ? "AllowedApps" : "DisallowedApps") + " = " + string.Join(", ", apps));
            }

            byte[] body = enc.GetBytes(string.Join(nl, lines) + (trailingNewline ? nl : ""));
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
            lastWriteName = Path.GetFileNameWithoutExtension(path);
            lastWriteAt = DateTime.UtcNow;
            try { File.Replace(tmp, path, null); }
            catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }
            ConfigInfo.Invalidate(lastWriteName);
        }

        static string lastWriteName;
        static DateTime lastWriteAt;

        // The configs watcher also sees the app's own writes; those are already reflected in memory
        public static bool IsOwnWrite(string name)
        {
            return lastWriteName != null && lastWriteName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                   (DateTime.UtcNow - lastWriteAt).TotalSeconds < 2;
        }

        // Collects private/CGNAT DNS IPs configured inside WireGuard configs (excluding public DNS 1.1.1.1/9.9.9.9/8.8.8.8).
        public static HashSet<string> CollectVpnPrivateDns()
        {
            var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            try
            {
                if (Directory.Exists(Paths.Configs)) files.AddRange(Directory.GetFiles(Paths.Configs, "*.conf"));
                if (Directory.Exists(Paths.Runtime)) files.AddRange(Directory.GetFiles(Paths.Runtime, "*.conf"));
            }
            catch { }
            foreach (string f in files)
            {
                try
                {
                    foreach (string line in File.ReadAllLines(f))
                    {
                        if (!DnsLine.IsMatch(line)) continue;
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        int cmt = line.IndexOf('#');
                        string raw = cmt > eq ? line.Substring(eq + 1, cmt - eq - 1) : line.Substring(eq + 1);
                        foreach (string part in raw.Split(','))
                        {
                            string item = part.Trim();
                            IPAddress ip;
                            if (!IPAddress.TryParse(item, out ip)) continue;
                            if (IsPrivateOrCgnatVpnDns(ip)) dns.Add(ip.ToString());
                        }
                    }
                }
                catch { }
            }
            return dns;
        }

        static bool IsPrivateOrCgnatVpnDns(IPAddress ip)
        {
            if (ip == null || IPAddress.IsLoopback(ip)) return false;
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork && b.Length == 4)
            {
                if (b[0] == 10) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                return false;
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && b.Length == 16)
            {
                return (b[0] & 0xFE) == 0xFC; // ULA fc00::/7
            }
            return false;
        }

        // Creates a unique ZIP backup and restores accidentally persisted split-tunnel DNS comments in user .conf files.
        public static int SanitizeLegacyConfs(out string backupZipPath)
        {
            backupZipPath = null;
            const string legacyDnsComment = "# vpn-panel: DNS local mantido em split tunneling";
            var dirty = new List<string>();
            try
            {
                if (!Directory.Exists(Paths.Configs)) return 0;
                foreach (string f in Directory.GetFiles(Paths.Configs, "*.conf"))
                {
                    try
                    {
                        string text = File.ReadAllText(f);
                        if (text.IndexOf(legacyDnsComment, StringComparison.OrdinalIgnoreCase) >= 0)
                            dirty.Add(f);
                    }
                    catch { }
                }
            }
            catch { return 0; }
            if (dirty.Count == 0) return 0;

            string zip = null;
            try
            {
                string backupsDir = Path.Combine(Paths.Data, "backups");
                Directory.CreateDirectory(backupsDir);
                zip = Path.Combine(backupsDir, "configs-pre-clean-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");
                using (var arch = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (string f in dirty)
                    {
                        byte[] raw = File.ReadAllBytes(f);
                        var entry = arch.CreateEntry(Path.GetFileName(f), System.IO.Compression.CompressionLevel.Optimal);
                        using (var es = entry.Open()) es.Write(raw, 0, raw.Length);
                    }
                }
                if (File.Exists(zip)) backupZipPath = zip;
            }
            catch
            {
                backupZipPath = null;
                if (!string.IsNullOrEmpty(zip) && File.Exists(zip)) { try { File.Delete(zip); } catch { } }
            }

            if (string.IsNullOrEmpty(backupZipPath)) return 0; // never modify user configs if backup failed

            int count = 0;
            foreach (string f in dirty)
            {
                string tmp = null;
                try
                {
                    byte[] raw = File.ReadAllBytes(f);
                    bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                    int offset = bom ? 3 : 0;
                    int byteLen = raw.Length - offset;
                    Encoding enc = new UTF8Encoding(false, true);
                    string text;
                    try { text = enc.GetString(raw, offset, byteLen); }
                    catch (DecoderFallbackException)
                    {
                        enc = Encoding.Default;
                        text = enc.GetString(raw, offset, byteLen);
                    }
                    string nl = text.Contains("\r\n") ? "\r\n" : "\n";
                    string[] srcLines = text.Split('\n');
                    var outLines = new List<string>(srcLines.Length);
                    bool modified = false;
                    for (int i = 0; i < srcLines.Length; i++)
                    {
                        string l = srcLines[i].TrimEnd('\r');
                        int idx = l.IndexOf(legacyDnsComment, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0 && l.TrimStart().StartsWith("#"))
                        {
                            string unwrapped = l.TrimStart().Substring(1).TrimStart();
                            int commentPos = unwrapped.IndexOf(legacyDnsComment, StringComparison.OrdinalIgnoreCase);
                            if (commentPos >= 0) unwrapped = unwrapped.Substring(0, commentPos).TrimEnd();
                            if (DnsLine.IsMatch(unwrapped))
                            {
                                outLines.Add(unwrapped);
                                modified = true;
                                continue;
                            }
                        }
                        outLines.Add(l);
                    }
                    if (!modified) continue;
                    byte[] body = enc.GetBytes(string.Join(nl, outLines.ToArray()));
                    tmp = f + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                    using (var fs = File.Create(tmp))
                    {
                        if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                        fs.Write(body, 0, body.Length);
                    }
                    try { File.Replace(tmp, f, null); }
                    catch (IOException) { File.Copy(tmp, f, true); File.Delete(tmp); }
                    tmp = null;
                    ConfigInfo.Invalidate(Path.GetFileNameWithoutExtension(f));
                    count++;
                }
                catch { }
                finally
                {
                    if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
                }
            }
            if (count > 0) Paths.ConfigsChanged();
            return count;
        }

        // Returns an error message, or null when the name can be used
        public static string CheckName(string name, string current)
        {
            if (name.Length == 0) return "Digite um nome.";
            if (name.Length > 60) return "Use no máximo 60 caracteres.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.StartsWith("."))
                return "O nome não pode ter os caracteres \\ / : * ? \" < > |";
            if (!name.Equals(current, StringComparison.OrdinalIgnoreCase) && File.Exists(Paths.Conf(name)))
                return "Já existe uma configuração com esse nome.";
            return null;
        }

        public static bool IsWireGuard(string text)
        {
            return text != null &&
                   Regex.IsMatch(text, @"^\s*\[Interface\]", RegexOptions.IgnoreCase | RegexOptions.Multiline) &&
                   Regex.IsMatch(text, @"^\s*\[Peer\]", RegexOptions.IgnoreCase | RegexOptions.Multiline) &&
                   Regex.IsMatch(text, @"^\s*PrivateKey\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        // Text of a .conf as bytes, BOM or not
        static string Decode(byte[] data)
        {
            using (var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        public static bool IsImportable(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".conf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        }

        // WireGuard configs inside a .conf or a .zip (Mullvad hands out a zip with many), as (name, bytes)
        public static List<KeyValuePair<string, byte[]>> ReadConfs(string source)
        {
            var found = new List<KeyValuePair<string, byte[]>>();
            if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var zip = System.IO.Compression.ZipFile.OpenRead(source))
                    foreach (var entry in zip.Entries)
                    {
                        if (!entry.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) || entry.Length > 64 * 1024) continue;
                        using (Stream st = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            st.CopyTo(ms);
                            byte[] entryBytes = ms.ToArray();
                            if (IsWireGuard(Decode(entryBytes)))
                                found.Add(new KeyValuePair<string, byte[]>(Path.GetFileNameWithoutExtension(entry.Name), entryBytes));
                        }
                    }
                return found;
            }
            if (new FileInfo(source).Length > 64 * 1024) return found;
            byte[] data = File.ReadAllBytes(source);
            if (IsWireGuard(Decode(data))) found.Add(new KeyValuePair<string, byte[]>(Path.GetFileNameWithoutExtension(source), data));
            return found;
        }

        // Copies the configs of a .conf or .zip into the configs folder and returns their names
        public static List<string> Import(string source)
        {
            var confs = ReadConfs(source);
            if (confs.Count == 0)
                throw new InvalidDataException(source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? "o .zip não tem nenhuma configuração WireGuard."
                    : "não parece uma configuração WireGuard (faltam [Interface], [Peer] ou PrivateKey).");
            return confs.Select(c => Save(c.Value, c.Key)).ToList();
        }

        // A config pasted as text
        public static string ImportText(string text, string name)
        {
            if (!IsWireGuard(text)) throw new InvalidDataException("o texto não parece uma configuração WireGuard.");
            return Save(new UTF8Encoding(false).GetBytes(text), name);
        }

        static string Save(byte[] data, string wanted)
        {
            string baseName = wanted.Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
            if (baseName.Length == 0) baseName = "config";
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60);
            string name = baseName;
            for (int n = 2; File.Exists(Paths.Conf(name)); n++) name = baseName + " (" + n + ")";
            File.WriteAllBytes(Paths.Conf(name), data);
            ConfigInfo.Invalidate(name);
            return name;
        }

        public static void Rename(string oldName, string newName)
        {
            string from = Paths.Conf(oldName), to = Paths.Conf(newName);
            if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
            {
                // case-only rename
                string tmp = from + ".rename";
                File.Move(from, tmp);
                File.Move(tmp, to);
            }
            else File.Move(from, to);
            ConfigInfo.Invalidate(oldName);
            ConfigInfo.Invalidate(newName);
        }

        public static void Delete(string name)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Paths.Conf(name),
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            ConfigInfo.Invalidate(name);
        }
    }
}
