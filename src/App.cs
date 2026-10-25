// VPN Panel - tray app for the WireSock WireGuard client (wiresock-client.exe).
// This file: entry point, storage, .conf handling, start-with-Windows and the WireSock process.
// The UI lives in MainWindow.cs / MainWindow.xaml / Theme.xaml. Build with build.ps1.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
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

[assembly: AssemblyTitle("VPN Panel")]
[assembly: AssemblyProduct("VPN Panel")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]

namespace VpnPanel
{
    static class Program
    {
        // Same mutex as the old vpn-panel.ps1, so they never run together
        const string MutexName = @"Global\VpnPanelWireSock";
        public const string ShowEventName = @"Global\VpnPanelWireSockShow";
        public const string Version = "2.2";

        [STAThread]
        static void Main(string[] args)
        {
            // The exe starts without admin rights (asInvoker) and hands off to an elevated copy:
            // through the "open" scheduled task when it exists (no UAC prompt), otherwise through UAC.
            // The task can't pass arguments, so --uninstall always goes through UAC.
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (args.Length == 0 && Tasks.PointsHere(Tasks.Open) && Tasks.Run(Tasks.Open)) return;
                try { Process.Start(new ProcessStartInfo(Paths.Exe, string.Join(" ", args)) { UseShellExecute = true, Verb = "runas" }); }
                catch (Win32Exception) { } // the user declined the UAC prompt
                return;
            }
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Installer.RunUninstall();
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Already running: ask that instance to show its window
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); }
                    catch { }
                    return;
                }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                System.Windows.Forms.Application.EnableVisualStyles();
                Paths.Init();

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources = (ResourceDictionary)XamlReader.Parse(Res.Text("Theme.xaml"));
                new MainWindow(args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)));
                app.Run();
            }
        }
    }

    static class Res
    {
        public static Stream Open(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("VpnPanel." + name);
        }

        public static string Text(string name)
        {
            using (var r = new StreamReader(Open(name))) return r.ReadToEnd();
        }
    }

    static class Paths
    {
        public static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Exe = Assembly.GetExecutingAssembly().Location;
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPN Panel");
        public static readonly string Configs = Path.Combine(Data, "configs");
        public static readonly string Settings = Path.Combine(Data, "settings.json");
        public static readonly string Log = Path.Combine(Data, "wiresock.log");
        public static readonly string GeoCache = Path.Combine(Data, "locations.json");
        public static bool FirstRun;

        public static void Init()
        {
            Directory.CreateDirectory(Configs);
            FirstRun = !File.Exists(Settings);
            // First run: bring in configs that sit next to the exe (portable copies and the old C:\VPN layout)
            if (FirstRun && ConfigNames().Length == 0)
            {
                foreach (string f in Directory.GetFiles(ExeDir, "*.conf").Where(IsConf))
                {
                    try { File.Copy(f, Path.Combine(Configs, Path.GetFileName(f))); } catch { }
                }
            }
        }

        public static string WireSock
        {
            get
            {
                string[] candidates =
                {
                    @"%ProgramFiles%\WireSock Secure Connect\sdk\wiresock-client.exe",
                    @"%ProgramFiles%\WireSock Secure Connect\wiresock-client.exe",
                    @"%ProgramFiles%\WireSock VPN Client\bin\wiresock-client.exe",
                };
                return candidates.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
            }
        }

        public static string WireSockVersion
        {
            get
            {
                string exe = WireSock;
                if (exe == null) return null;
                try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion; } catch { return "?"; }
            }
        }

        static bool IsConf(string f)
        {
            return Path.GetExtension(f).Equals(".conf", StringComparison.OrdinalIgnoreCase);
        }

        public static string Conf(string name) { return Path.Combine(Configs, name + ".conf"); }

        public static string[] ConfigNames()
        {
            return Directory.GetFiles(Configs, "*.conf").Where(IsConf)
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    class Settings
    {
        public string Config;
        public bool NetworkLock;
        public bool AutoConnect;
        public bool AutoReconnect = true;
        public bool SkipUac = true;

        public static Settings Load()
        {
            var s = new Settings();
            // on first run, pick up the settings of the old C:\VPN version
            string file = Paths.FirstRun ? Path.Combine(Paths.ExeDir, "vpn-panel.json") : Paths.Settings;
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                object v;
                if (d.TryGetValue("config", out v)) s.Config = v as string;
                if (d.TryGetValue("networkLock", out v) && v is bool) s.NetworkLock = (bool)v;
                if (d.TryGetValue("autoConnect", out v) && v is bool) s.AutoConnect = (bool)v;
                if (d.TryGetValue("autoReconnect", out v) && v is bool) s.AutoReconnect = (bool)v;
                if (d.TryGetValue("skipUac", out v) && v is bool) s.SkipUac = (bool)v;
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var d = new Dictionary<string, object>
                {
                    { "config", Config }, { "networkLock", NetworkLock }, { "autoConnect", AutoConnect },
                    { "autoReconnect", AutoReconnect }, { "skipUac", SkipUac },
                };
                File.WriteAllText(Paths.Settings, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }
    }

    enum AppMode { All, Only, Except }

    // What the UI shows about one config
    class ConfigInfo
    {
        public string Name;
        public string Server;    // first comment in [Peer], e.g. "# US-FREE#40" in Proton configs
        public string Endpoint;  // host only
        public AppMode Mode;
        public List<string> Apps = new List<string>();
        public bool Readable;

        public static ConfigInfo Read(string name)
        {
            var c = new ConfigInfo { Name = name };
            try
            {
                bool inPeer = false;
                foreach (string line in File.ReadAllLines(Paths.Conf(name)))
                {
                    Match m = ConfFile.SectionLine.Match(line);
                    if (m.Success) { inPeer = m.Groups[1].Value.Equals("Peer", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inPeer) continue;
                    m = Regex.Match(line, @"^\s*#\s*(.+?)\s*$");
                    if (m.Success && c.Server == null && m.Groups[1].Value.Length <= 40 && !m.Groups[1].Value.Contains(" the "))
                        c.Server = m.Groups[1].Value;
                    m = Regex.Match(line, @"^\s*Endpoint\s*=\s*(\S+)", RegexOptions.IgnoreCase);
                    if (m.Success && c.Endpoint == null)
                    {
                        string ep = m.Groups[1].Value;
                        c.Endpoint = ep.StartsWith("[") ? ep.Substring(1, Math.Max(0, ep.IndexOf(']') - 1))
                                   : ep.Contains(':') ? ep.Substring(0, ep.LastIndexOf(':')) : ep;
                    }
                }
                c.Mode = ConfFile.ReadApps(Paths.Conf(name), c.Apps);
                c.Readable = true;
            }
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

        public string Location
        {
            get
            {
                Geo.Place p = Place;
                if (p == null) return null;
                return string.IsNullOrEmpty(p.City) ? p.CountryName : p.CountryName + " · " + p.City;
            }
        }

        public string AppsSummary
        {
            get
            {
                if (Mode == AppMode.Only) return "Só " + string.Join(", ", Apps);
                if (Mode == AppMode.Except) return "Todos, exceto " + string.Join(", ", Apps);
                return "Todos os apps";
            }
        }
    }

    // Config files: import/rename/delete, and the split tunneling keys (AllowedApps / DisallowedApps)
    // in the [Peer] section, leaving every other line of the file untouched.
    static class ConfFile
    {
        public static readonly Regex SectionLine = new Regex(@"^\s*\[(\w+)\]\s*$");
        static readonly Regex AppsLine = new Regex(@"^\s*(AllowedApps|DisallowedApps)\s*=\s*([^#]*)", RegexOptions.IgnoreCase);
        static readonly Regex AllowedIpsLine = new Regex(@"^\s*AllowedIPs\s*=", RegexOptions.IgnoreCase);

        static bool IsPeer(Match section)
        {
            return section.Groups[1].Value.Equals("Peer", StringComparison.OrdinalIgnoreCase);
        }

        public static AppMode ReadApps(string path, List<string> apps)
        {
            apps.Clear();
            var mode = AppMode.All;
            bool inPeer = false;
            foreach (string line in File.ReadAllLines(path))
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
            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var utf8 = new UTF8Encoding(false);
            string text = utf8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            bool trailingNewline = text.EndsWith("\n");
            List<string> lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (trailingNewline) lines.RemoveAt(lines.Count - 1);

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
            if (mode != AppMode.All && apps.Count > 0)
                lines.Insert(insertAt, (mode == AppMode.Only ? "AllowedApps" : "DisallowedApps") + " = " + string.Join(", ", apps));

            byte[] body = utf8.GetBytes(string.Join(nl, lines) + (trailingNewline ? nl : ""));
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
            try { File.Replace(tmp, path, null); }
            catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }
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

        // Copies a .conf into the configs folder and returns its name
        public static string Import(string source)
        {
            string text = File.ReadAllText(source);
            if (!Regex.IsMatch(text, @"^\s*\[Interface\]", RegexOptions.IgnoreCase | RegexOptions.Multiline) ||
                !Regex.IsMatch(text, @"^\s*\[Peer\]", RegexOptions.IgnoreCase | RegexOptions.Multiline) ||
                !Regex.IsMatch(text, @"^\s*PrivateKey\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline))
                throw new InvalidDataException("não parece uma configuração WireGuard (faltam [Interface], [Peer] ou PrivateKey).");

            string baseName = Path.GetFileNameWithoutExtension(source).Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
            if (baseName.Length == 0) baseName = "config";
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60);
            string name = baseName;
            for (int n = 2; File.Exists(Paths.Conf(name)); n++) name = baseName + " (" + n + ")";
            File.Copy(source, Paths.Conf(name));
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
        }

        public static void Delete(string name)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Paths.Conf(name),
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    // Scheduled tasks that run the exe with admin rights without a UAC prompt:
    //  - Logon: "start with Windows" (Windows won't elevate programs from the Run registry key)
    //  - Open: started by the non-elevated exe on every manual launch, so only the first launch asks
    // Uses the Task Scheduler COM API, which also works from the non-elevated exe.
    static class Tasks
    {
        public const string Logon = "VPN Panel";
        public const string Open = "VPN Panel - Abrir";

        static dynamic Folder()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            return service.GetFolder("\\");
        }

        static dynamic Get(string name)
        {
            try { return Folder().GetTask(name); } catch { return null; }
        }

        public static bool Exists(string name) { return Get(name) != null; }

        // Program the task starts, or null
        public static string CommandOf(string name)
        {
            try
            {
                dynamic task = Get(name);
                if (task == null) return null;
                foreach (dynamic action in task.Definition.Actions) return (string)action.Path;
            }
            catch { }
            return null;
        }

        public static bool PointsHere(string name)
        {
            string cmd = CommandOf(name);
            return cmd != null && cmd.Equals(Paths.Exe, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Run(string name)
        {
            try
            {
                dynamic task = Get(name);
                if (task == null) return false;
                task.Run(null);
                return true;
            }
            catch { return false; }
        }

        public static void Create(string name)
        {
            bool logon = name == Logon;
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>" +
                (logon ? "Abre o VPN Panel na bandeja ao entrar no Windows." : "Abre o VPN Panel sem o aviso de permissão do Windows.") +
                "</Description></RegistrationInfo>\r\n" +
                (logon ? "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>\r\n" : "") +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Paths.Exe) + "</Command>" +
                (logon ? "<Arguments>--tray</Arguments>" : "") + "</Exec></Actions>\r\n" +
                "</Task>\r\n";
            // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
            Folder().RegisterTask(name, xml, 6, null, null, 3, null);
        }

        public static void Delete(string name)
        {
            if (Exists(name)) Folder().DeleteTask(name, 0);
        }

        // Re-point existing tasks at this exe if it was moved, and add/remove the "open" task
        public static void Sync(bool skipUac)
        {
            try
            {
                if (Exists(Logon) && !PointsHere(Logon)) Create(Logon);
                if (skipUac && !PointsHere(Open)) Create(Open);
                if (!skipUac) Delete(Open);
            }
            catch { }
        }
    }

    // Country/city of server endpoints (ipinfo.io), cached on disk so cards show it instantly
    static class Geo
    {
        public class Place
        {
            public string Country, City;

            public string CountryName
            {
                get
                {
                    try { return new RegionInfo(Country).DisplayName; } catch { return Country; }
                }
            }
        }

        static Dictionary<string, Place> cache;
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void Load()
        {
            if (cache != null) return;
            cache = new Dictionary<string, Place>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(Paths.GeoCache));
                foreach (var kv in d)
                    cache[kv.Key] = new Place { Country = Convert.ToString(kv.Value["country"]), City = Convert.ToString(kv.Value["city"]) };
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                var d = cache.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object> { { "country", kv.Value.Country }, { "city", kv.Value.City } });
                File.WriteAllText(Paths.GeoCache, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }

        public static Place Cached(string host)
        {
            Load();
            Place p;
            return cache.TryGetValue(host, out p) ? p : null;
        }

        // Looks the host up in the background; onFound runs on the UI thread when a new place is known
        public static void Lookup(string host, Action onFound)
        {
            Load();
            if (cache.ContainsKey(host) || !pending.Add(host)) return;
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            new Thread(() =>
            {
                Place place = null;
                try
                {
                    IPAddress ip;
                    if (!IPAddress.TryParse(host, out ip)) ip = Dns.GetHostAddresses(host).First();
                    using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                    {
                        wc.Headers["User-Agent"] = "vpn-panel";
                        var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wc.DownloadString("https://ipinfo.io/" + ip + "/json"));
                        object country, city;
                        if (j.TryGetValue("country", out country))
                            place = new Place { Country = Convert.ToString(country), City = j.TryGetValue("city", out city) ? Convert.ToString(city) : "" };
                    }
                }
                catch { }
                ui.BeginInvoke(new Action(() =>
                {
                    pending.Remove(host);
                    if (place == null) return;
                    cache[host] = place;
                    Save();
                    onFound();
                }));
            }) { IsBackground = true }.Start();
        }
    }

    // The wiresock-client process
    static class Tunnel
    {
        public static Process[] Find() { return Process.GetProcessesByName("wiresock-client"); }

        public static string CommandLine(int pid)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                    foreach (ManagementObject o in searcher.Get()) return Convert.ToString(o["CommandLine"]);
            }
            catch { }
            return null;
        }

        // Config name from a client command line (also catches tunnels started outside the app)
        public static string ConfigOf(string commandLine)
        {
            if (commandLine == null) return null;
            Match m = Regex.Match(commandLine, "-config\\s+\"?(.+?\\.conf)\"?(\\s|$)", RegexOptions.IgnoreCase);
            return m.Success ? Path.GetFileNameWithoutExtension(m.Groups[1].Value) : null;
        }

        public static void Start(string exe, string config, bool networkLock)
        {
            // "info" logs handshakes, which is how the app knows the server really answered
            string args = "run -config \"" + Paths.Conf(config) + "\" -log-level info";
            if (networkLock) args += " -network-lock enabled";
            // cmd.exe redirects the output to the log and keeps the tunnel alive if the panel exits
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + exe + "\" " + args + " > \"" + Paths.Log + "\" 2>&1\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi).Dispose();
        }

        public static void Stop()
        {
            foreach (Process p in Find())
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }
    }

    // Borderless dark window with native shadow and rounded corners (Windows 11)
    static class Chrome
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window w, double captionHeight)
        {
            WindowChrome.SetWindowChrome(w, new WindowChrome
            {
                CaptionHeight = captionHeight,
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            w.SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int dark = 1, round = 2, border = 0x00352B26; // COLORREF is 0x00BBGGRR
                DwmSetWindowAttribute(h, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
                DwmSetWindowAttribute(h, 34, ref border, 4);  // DWMWA_BORDER_COLOR
            };
        }
    }
}
