// VPN Panel - Lan: the optional "Rede de amigos", a virtual LAN over the internet (what Radmin VPN does).
// The engine is EasyTier (easytier-core.exe / easytier-cli.exe, LGPL-3.0): downloaded from its GitHub release on
// first use, accepted only when the zip matches the SHA-256 pinned here, and run as a child process.
// Nothing here touches the VPN tunnel: turning the LAN on or off never restarts WireSock / Wintun.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VpnPanel
{
    // One virtual network: what an invite carries and what settings.json remembers
    class LanNetwork
    {
        public string Name;     // what people see ("Galera")
        public string Id;       // EasyTier network name ("vp-galera-x7k2qd")
        public string Secret;   // EasyTier network secret; never logged or shown
        public List<string> Nodes = new List<string>(); // meeting nodes ("udp://host:port")
    }

    class LanPeer
    {
        public string Name, Ip;
        public bool Direct;      // straight to the other PC; false = relayed through the meeting node
        public double LatencyMs; // -1 when unknown
    }

    class LanStatus
    {
        public string MyIp;
        public bool NodeSeen; // at least one meeting node answered
        public List<LanPeer> Peers = new List<LanPeer>();
    }

    static class Lan
    {
        public const string AdapterName = "VPN Panel LAN";
        public const string Subnet = "10.126.126.0/24"; // EasyTier's default DHCP range
        public const string EngineVersion = "2.6.4";
        public const string InvitePrefix = "VPL1-";

        const string ZipUrl = "https://github.com/EasyTier/EasyTier/releases/download/v{0}/easytier-windows-{1}-v{0}.zip";
        const string FirewallGroup = "VPN Panel";
        const string ProtectRule = "VPN Panel LAN - proteger este PC";
        // WinDivert64.sys also ships in the zip but is only for --enable-udp-broadcast-relay, which this app does not use
        static readonly string[] EngineFiles = { "easytier-core.exe", "easytier-cli.exe", "wintun.dll", "Packet.dll" };

        // SHA-256 of the official release zips (GitHub's own asset digests for v2.6.4)
        static string ZipHash(string arch)
        {
            if (arch == "arm64") return "37023f8a3451c9234b17ee2089a03dc344ce90d803b5b359cb6c46682b0549b4";
            if (arch == "i686") return "bf557daeccc5525d95b8a230c339d75554fb52d82d1e050e9a5202c92c02e09e";
            return "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84";
        }

        static string Arch()
        {
            string p = SetupUtil.Platform();
            return p == "ARM64" ? "arm64" : p == "x86" ? "i686" : "x86_64";
        }

        // Installed copy: next to the exe (Program Files, writable by admins only). Portable copy: the data folder.
        public static string BinDir { get { return Path.Combine(Paths.IsInstalled ? Paths.ExeDir : Paths.Data, "lan"); } }
        public static string Core { get { return Path.Combine(BinDir, "easytier-core.exe"); } }
        static string Cli { get { return Path.Combine(BinDir, "easytier-cli.exe"); } }
        static string VersionFile { get { return Path.Combine(BinDir, "engine.version"); } }
        static string StateFile { get { return Path.Combine(Paths.Runtime, "lan.state"); } }
        public static string LogPath { get { return Path.Combine(Paths.Data, "lan.log"); } }

        public static bool Installed
        {
            get
            {
                try
                {
                    string dir = BinDir;
                    foreach (string f in EngineFiles)
                        if (!File.Exists(Path.Combine(dir, f))) return false;
                    return File.ReadAllText(VersionFile).Trim() == EngineVersion;
                }
                catch { return false; }
            }
        }

        // ---------- install ----------

        // Returns null when the engine ends up installed, otherwise a message for the user.
        // status gets short progress lines; it is called from this (background) thread.
        public static string Install(Action<string> status)
        {
            string arch = Arch();
            string temp = Path.Combine(Paths.Data, "temp");
            string zip = Path.Combine(temp, "lan-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            string stage = zip + ".x";
            try
            {
                Directory.CreateDirectory(temp);
                status("Baixando o componente da rede...");
                Download(string.Format(ZipUrl, EngineVersion, arch), zip,
                    pct => status("Baixando o componente da rede... " + pct + "%"));
                status("Conferindo o arquivo...");
                if (!Sha256(zip).Equals(ZipHash(arch), StringComparison.OrdinalIgnoreCase))
                    return "O arquivo baixado não confere com o original do EasyTier, então não foi instalado.";

                Directory.CreateDirectory(stage);
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (EngineFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                            entry.ExtractToFile(Path.Combine(stage, entry.Name), true);
                    }
                }
                foreach (string f in EngineFiles)
                    if (!File.Exists(Path.Combine(stage, f))) return "O pacote do EasyTier veio sem o arquivo " + f + ".";

                string dir = BinDir;
                Directory.CreateDirectory(dir);
                try { File.Delete(VersionFile); } catch { }
                foreach (string f in EngineFiles)
                {
                    string dst = Path.Combine(dir, f);
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(Path.Combine(stage, f), dst);
                }
                File.WriteAllText(VersionFile, EngineVersion);
                return null;
            }
            catch (WebException ex) { return "Não deu para baixar o componente da rede: " + ex.Message; }
            catch (Exception ex) { return "Não deu para instalar o componente da rede: " + ex.Message; }
            finally
            {
                try { File.Delete(zip); } catch { }
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            }
        }

        static void Download(string url, string file, Action<int> progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "VPN-Panel";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = resp.ContentLength, done = 0;
                int last = -1, n;
                byte[] buf = new byte[81920];
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    if (total <= 0) continue;
                    int pct = (int)(done * 100 / total);
                    if (pct != last) { last = pct; progress(pct); }
                }
            }
        }

        static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        // ---------- process ----------

        static readonly object gate = new object();
        static IntPtr handle = IntPtr.Zero;
        static int pid, rpcPort;

        public static bool Running { get { return ProcUtil.IsAlive(handle); } }

        // Returns null when the engine is running, otherwise a message for the user.
        public static string Start(LanNetwork net, string nick, bool protect)
        {
            lock (gate)
            {
                if (Running) return null;
                if (net == null || !ValidNetwork(net)) return "Esta rede está com os dados incompletos. Entre de novo com o convite.";
                if (!Installed) return "O componente da rede não está instalado.";
                Firewall.Cleanup(); // leftovers of a run that did not stop cleanly (reboot, crash)
                try
                {
                    int port = FreePort();
                    var sb = new StringBuilder();
                    sb.Append('"').Append(Core).Append('"');
                    sb.Append(" --network-name ").Append(net.Id);
                    sb.Append(" -d");
                    foreach (string node in net.Nodes) sb.Append(" -p ").Append(node);
                    sb.Append(" --hostname ").Append(CleanNick(nick));
                    sb.Append(" --dev-name \"").Append(AdapterName).Append('"');
                    // random ports: direct connections from friends on the same LAN, IPv6 or UPnP still get in
                    sb.Append(" -l udp://0.0.0.0:0 tcp://0.0.0.0:0");
                    sb.Append(" -r 127.0.0.1:").Append(port);
                    sb.Append(" --console-log-level warn");

                    Directory.CreateDirectory(Paths.Runtime);
                    int newPid;
                    IntPtr h;
                    // The secret goes in the environment, not on the command line (which other users can read)
                    Environment.SetEnvironmentVariable("ET_NETWORK_SECRET", net.Secret);
                    try { h = ProcUtil.StartLogged(Core, sb.ToString(), LogPath, out newPid); }
                    finally { Environment.SetEnvironmentVariable("ET_NETWORK_SECRET", null); }
                    handle = h;
                    pid = newPid;
                    rpcPort = port;
                    try { File.WriteAllText(StateFile, newPid + "|" + port); } catch { }
                }
                catch (Exception ex) { return "Não deu para iniciar a rede: " + ex.Message; }
            }
            if (protect) Firewall.Protect(true);
            return null;
        }

        // An engine left running by a previous instance of the panel (the panel can quit and leave the LAN on)
        public static bool Adopt()
        {
            lock (gate)
            {
                if (Running) return true;
                try
                {
                    if (!File.Exists(StateFile)) return false;
                    string[] parts = File.ReadAllText(StateFile).Split('|');
                    int p = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    int port = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    string exe = ProcUtil.ExePath(p);
                    if (exe != null && exe.Equals(Core, StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr h = ProcUtil.OpenForMonitor(p);
                        if (ProcUtil.IsAlive(h))
                        {
                            ProcUtil.Close(ref handle);
                            handle = h;
                            pid = p;
                            rpcPort = port;
                            return true;
                        }
                        ProcUtil.Close(ref h);
                    }
                }
                catch { }
                return false;
            }
        }

        // The state file outlived its process: the last run did not go through Stop()
        public static bool StaleState { get { return !Running && File.Exists(StateFile); } }

        public static void Stop()
        {
            lock (gate)
            {
                // Ctrl+C first, so EasyTier removes its adapter and routes by itself
                if (pid != 0 && Running) ProcUtil.Shutdown(pid, 3000);
                ProcUtil.Close(ref handle);
                pid = 0;
                rpcPort = 0;
                try { File.Delete(StateFile); } catch { }
            }
            Firewall.Cleanup();
        }

        // Uninstall: whatever is running from our folder goes, known to this process or not
        public static void StopForUninstall()
        {
            Adopt();
            Stop();
            try
            {
                foreach (Process p in Process.GetProcessesByName("easytier-core"))
                {
                    try
                    {
                        string exe = ProcUtil.ExePath(p.Id);
                        if (exe != null && exe.Equals(Core, StringComparison.OrdinalIgnoreCase)) ProcUtil.Shutdown(p.Id, 2000);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            Firewall.Cleanup();
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try { return ((IPEndPoint)l.LocalEndpoint).Port; }
            finally { l.Stop(); }
        }

        // Last line the engine wrote, for the "it stopped by itself" message
        public static string LastLogLine()
        {
            try
            {
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int take = (int)Math.Min(fs.Length, 4096);
                    fs.Seek(-take, SeekOrigin.End);
                    byte[] buf = new byte[take];
                    int read = fs.Read(buf, 0, take);
                    string[] lines = Encoding.UTF8.GetString(buf, 0, read).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) return null;
                    string last = Regex.Replace(lines[lines.Length - 1], @"\x1B\[[0-9;]*m", "").Trim();
                    return last.Length > 220 ? last.Substring(0, 220) + "..." : last;
                }
            }
            catch { return null; }
        }

        // ---------- status ----------

        // Who is in the network right now (easytier-cli over the local RPC port); null when the engine did not answer.
        // Only "peer" is ever asked: the "node" command prints the whole config, secret included.
        public static LanStatus Query()
        {
            int port = rpcPort;
            if (port == 0 || !Running) return null;
            try
            {
                string stdout, stderr;
                if (ProcUtil.RunBounded(Cli, "-p 127.0.0.1:" + port + " -o json peer", 4000, out stdout, out stderr) != 0) return null;
                var rows = new JavaScriptSerializer().DeserializeObject(stdout) as object[];
                if (rows == null) return null;
                var st = new LanStatus();
                foreach (object row in rows)
                {
                    var d = row as Dictionary<string, object>;
                    if (d == null) continue;
                    string ip = Str(d, "ipv4"), name = Str(d, "hostname"), cost = Str(d, "cost");
                    if (cost.Equals("Local", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ip.Length > 0) st.MyIp = ip;
                        continue;
                    }
                    // meeting nodes take no address in the network
                    if (ip.Length == 0)
                    {
                        st.NodeSeen = true;
                        continue;
                    }
                    double lat;
                    if (!double.TryParse(Str(d, "lat_ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out lat)) lat = -1;
                    st.Peers.Add(new LanPeer
                    {
                        Name = name.Length > 0 ? name : ip,
                        Ip = ip,
                        Direct = cost.StartsWith("p2p", StringComparison.OrdinalIgnoreCase),
                        LatencyMs = lat,
                    });
                }
                st.Peers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return st;
            }
            catch { return null; }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : "";
        }

        public static bool IsLanAdapter(NetworkInterface ni)
        {
            if (ni == null) return false;
            return AdapterName.Equals(ni.Name, StringComparison.OrdinalIgnoreCase) ||
                   (ni.Description ?? "").StartsWith(AdapterName, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- networks and invites ----------

        static readonly Regex IdPattern = new Regex(@"^vp-[a-z0-9-]{3,40}$", RegexOptions.Compiled);
        static readonly Regex SecretPattern = new Regex(@"^[A-Za-z0-9_-]{12,128}$", RegexOptions.Compiled);
        static readonly Regex NodePattern = new Regex(
            @"^(tcp|udp|ws|wss|quic|wg)://([A-Za-z0-9][A-Za-z0-9.-]{0,252}|\[[0-9A-Fa-f:]{2,45}\]):(\d{1,5})(/[A-Za-z0-9._/-]{0,64})?$",
            RegexOptions.Compiled);

        // "host:port" or a full "udp://host:port" -> the canonical form, or null when it is not an address
        public static string NormalizeNode(string text)
        {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return null;
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "udp://" + s;
            Match m = NodePattern.Match(s);
            if (!m.Success) return null;
            int port;
            if (!int.TryParse(m.Groups[3].Value, out port) || port < 1 || port > 65535) return null;
            return m.Groups[1].Value.ToLowerInvariant() + s.Substring(m.Groups[1].Length);
        }

        static bool ValidNetwork(LanNetwork n)
        {
            if (n == null || n.Id == null || n.Secret == null || n.Nodes == null) return false;
            if (!IdPattern.IsMatch(n.Id) || !SecretPattern.IsMatch(n.Secret)) return false;
            if (n.Nodes.Count < 1 || n.Nodes.Count > 4) return false;
            foreach (string node in n.Nodes)
                if (node == null || !NodePattern.IsMatch(node)) return false;
            return true;
        }

        public static string CleanName(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in (text ?? "").Trim())
                if (!char.IsControl(c)) sb.Append(c);
            string s = sb.ToString();
            return s.Length > 32 ? s.Substring(0, 32).Trim() : s;
        }

        // The name other people see in the list: plain ASCII, no spaces (it travels as a command-line argument)
        public static string CleanNick(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in (text ?? "").Trim().Normalize(NormalizationForm.FormD))
            {
                if (sb.Length >= 24) break;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
                else if ((c == ' ' || c == '-' || c == '.') && sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            string s = sb.ToString().Trim('-');
            return s.Length > 0 ? s : "PC";
        }

        public static LanNetwork NewNetwork(string name, IEnumerable<string> nodes)
        {
            var slug = new StringBuilder();
            foreach (char c in CleanNick(name).ToLowerInvariant())
            {
                if (slug.Length >= 16) break;
                slug.Append(c == '_' ? '-' : c);
            }
            byte[] rnd = new byte[28];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
            var tail = new StringBuilder();
            for (int i = 0; i < 6; i++) tail.Append(alphabet[rnd[i] % alphabet.Length]);
            return new LanNetwork
            {
                Name = CleanName(name),
                Id = "vp-" + slug.ToString().Trim('-') + "-" + tail,
                Secret = Base64Url(rnd, 4, 24),
                Nodes = new List<string>(nodes),
            };
        }

        public static string MakeInvite(LanNetwork n)
        {
            var d = new Dictionary<string, object> { { "n", n.Name }, { "i", n.Id }, { "s", n.Secret }, { "p", n.Nodes } };
            byte[] raw = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(d));
            return InvitePrefix + Base64Url(raw, 0, raw.Length);
        }

        // Returns the network, or null with a message saying what is wrong with the text
        public static LanNetwork ParseInvite(string text, out string error)
        {
            error = "Isso não parece um convite do VPN Panel. Ele começa com " + InvitePrefix + ".";
            string s = Regex.Replace(text ?? "", @"\s+", "");
            int at = s.IndexOf(InvitePrefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            s = s.Substring(at + InvitePrefix.Length);
            try
            {
                string b64 = s.Replace('-', '+').Replace('_', '/');
                while (b64.Length % 4 != 0) b64 += "=";
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                var net = new LanNetwork { Name = CleanName(Str(d, "n")), Id = Str(d, "i"), Secret = Str(d, "s") };
                object nodes;
                var list = d.TryGetValue("p", out nodes) ? nodes as System.Collections.IEnumerable : null;
                if (list != null && !(list is string))
                {
                    foreach (object o in list)
                    {
                        string node = NormalizeNode(o as string);
                        if (node != null && !net.Nodes.Contains(node)) net.Nodes.Add(node);
                    }
                }
                if (net.Name.Length == 0) net.Name = net.Id;
                if (ValidNetwork(net)) { error = null; return net; }
            }
            catch { }
            error = "O convite está incompleto ou foi cortado. Peça para copiarem de novo.";
            return null;
        }

        static string Base64Url(byte[] data, int offset, int count)
        {
            return Convert.ToBase64String(data, offset, count).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---------- Windows Firewall ----------

        // EasyTier adds its own rules (group "EasyTier": everything allowed on the virtual adapter, plus the exe)
        // and never removes them. Stopping the LAN takes them out again, so "off" leaves nothing behind.
        static class Firewall
        {
            const int ProtocolTcp = 6, ProtocolUdp = 17, DirectionIn = 1, ActionBlock = 0, AllProfiles = 0x7FFFFFFF;

            static dynamic Policy()
            {
                return Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
            }

            public static void Cleanup()
            {
                try
                {
                    dynamic rules = Policy().Rules;
                    string core = Core;
                    var names = new List<string>();
                    foreach (dynamic r in rules)
                    {
                        try
                        {
                            string name = (string)r.Name ?? "";
                            string group = (string)r.Grouping ?? "";
                            string app = (string)r.ApplicationName ?? "";
                            bool ours = name.StartsWith(ProtectRule, StringComparison.Ordinal) ||
                                        app.Equals(core, StringComparison.OrdinalIgnoreCase) ||
                                        (group == "EasyTier" && name.StartsWith("EasyTier " + AdapterName + " ", StringComparison.Ordinal));
                            if (ours) names.Add(name);
                        }
                        catch { }
                    }
                    // Remove() takes one rule per call, and names repeat (TCP + UDP)
                    foreach (string name in names)
                        try { rules.Remove(name); } catch { }
                }
                catch { }
            }

            // Friends in the network reach games, not this PC's shared folders or remote desktop.
            // A block rule wins over EasyTier's "allow everything on the adapter".
            public static void Protect(bool on)
            {
                try
                {
                    dynamic rules = Policy().Rules;
                    for (int i = 0; i < 2; i++)
                    {
                        try { rules.Remove(ProtectRule + " (TCP)"); } catch { }
                        try { rules.Remove(ProtectRule + " (UDP)"); } catch { }
                    }
                    if (!on) return;
                    rules.Add(BlockRule(" (TCP)", ProtocolTcp, "135,139,445,3389,5985,5986"));
                    rules.Add(BlockRule(" (UDP)", ProtocolUdp, "137,138,3389"));
                }
                catch { }
            }

            static dynamic BlockRule(string suffix, int protocol, string ports)
            {
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                rule.Name = ProtectRule + suffix;
                rule.Description = "Rede de amigos do VPN Panel: bloqueia pastas compartilhadas e área de trabalho remota para quem está na rede.";
                rule.Protocol = protocol; // before the ports: they only exist for TCP/UDP
                rule.LocalPorts = ports;
                rule.RemoteAddresses = Subnet;
                rule.Direction = DirectionIn;
                rule.Action = ActionBlock;
                rule.Profiles = AllProfiles;
                rule.Grouping = FirewallGroup;
                rule.Enabled = true;
                return rule;
            }
        }

        public static void SetProtect(bool on) { Firewall.Protect(on); }

        public static void CleanupFirewall() { Firewall.Cleanup(); }
    }
}
