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

[assembly: AssemblyTitle("VPN Panel")]
[assembly: AssemblyDescription("Painel para WireSock WireGuard no Windows")]
[assembly: AssemblyCompany("VPN Panel")]
[assembly: AssemblyProduct("VPN Panel")]
[assembly: AssemblyCopyright("Copyright © 2026 VPN Panel")]
[assembly: AssemblyTrademark("VPN Panel")]
[assembly: ComVisible(false)]
[assembly: Guid("b7c93e48-8120-4a87-8d2b-65c71d798a21")]
[assembly: AssemblyVersion("3.0.3.0")]
[assembly: AssemblyFileVersion("3.0.3.0")]

namespace VpnPanel
{
    static class Program
    {
        // Same mutex as the old vpn-panel.ps1, so they never run together
        const string MutexName = @"Global\VpnPanelWireSock";
        public const string ShowEventName = @"Global\VpnPanelWireSockShow";
        public const string Version = "3.0.3";

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogCrash("AppDomain", e.ExceptionObject as Exception);
            };

            // The exe starts without admin rights (asInvoker) and hands off to an elevated copy:
            // through the "open" scheduled task when it exists (no UAC prompt), otherwise through UAC.
            // The task can't pass arguments, so --uninstall always goes through UAC.
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (args.Length == 0 && Paths.IsInstalled && Tasks.PointsHere(Tasks.Open) && Tasks.Run(Tasks.Open)) return;
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
                app.DispatcherUnhandledException += (s, e) =>
                {
                    LogCrash("Dispatcher", e.Exception);
                    e.Handled = true;
                };
                app.Resources = (ResourceDictionary)XamlReader.Parse(Res.Text("Theme.xaml"));
                new MainWindow(args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)));
                app.Run();
            }
        }

        public static void LogCrash(string source, Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Paths.Data);
                string crashFile = Path.Combine(Paths.Data, "crash.log");
                try
                {
                    var fi = new FileInfo(crashFile);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        byte[] all = File.ReadAllBytes(crashFile);
                        int keep = 128 * 1024;
                        int start = all.Length - keep;
                        using (var fs = new FileStream(crashFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                            fs.Write(all, start, keep);
                    }
                }
                catch { }
                var sb = new StringBuilder();
                sb.AppendFormat("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}: {3}\r\n{4}\r\n",
                    DateTime.Now, source, ex.GetType().FullName, ex.Message, ex.StackTrace);
                Exception inner = ex.InnerException;
                while (inner != null)
                {
                    sb.AppendFormat("Caused by: {0}: {1}\r\n{2}\r\n", inner.GetType().FullName, inner.Message, inner.StackTrace);
                    inner = inner.InnerException;
                }
                sb.AppendLine();
                File.AppendAllText(crashFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
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
        public static readonly string Runtime = Path.Combine(Data, "runtime");
        public static readonly string Settings = Path.Combine(Data, "settings.json");
        public static readonly string Log = Path.Combine(Data, "wiresock.log");
        public static readonly string GeoCache = Path.Combine(Data, "locations.json");
        public static readonly string NetworkLockFlag = Path.Combine(Data, "network.lock");
        public static bool FirstRun;

        public static bool IsInstalled
        {
            get
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string expectedDir = Path.Combine(programFiles, "VPN Panel");
                return ExeDir.Equals(expectedDir, StringComparison.OrdinalIgnoreCase);
            }
        }

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
                ConfigsChanged();
            }
        }

        static readonly string[] WireSockCandidates = new[]
        {
            @"%ProgramFiles%\WireSock Secure Connect\sdk\wiresock-client.exe",
            @"%ProgramFiles%\WireSock Secure Connect\bin\wiresock-client.exe",
            @"%ProgramFiles%\WireSock Secure Connect\wiresock-client.exe",
            @"%ProgramFiles%\WireSock VPN Client\bin\wiresock-client.exe",
        }.Select(Environment.ExpandEnvironmentVariables).ToArray();

        static string wireSockFound;
        static DateTime wireSockSearched;
        static DateTime wireSockVerified;
        public static bool SearchingFast;

        // Look again right away (after installing WireSock)
        public static void ForgetWireSock()
        {
            wireSockFound = null;
            wireSockSearched = DateTime.MinValue;
            wireSockVerified = DateTime.MinValue;
        }

        // wiresock-client.exe from any WireSock package: the SDK (no UI), the full app with UI,
        // or the older VPN Client. Known spots first, then a search of WireSock's install folders.
        public static string WireSock
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                if (wireSockFound != null)
                {
                    if ((now - wireSockVerified).TotalSeconds < 30) return wireSockFound;
                    if (File.Exists(wireSockFound)) { wireSockVerified = now; return wireSockFound; }
                    wireSockFound = null;
                }
                // searching is cheap but not free: 3 s while installing, 30 s otherwise
                if ((now - wireSockSearched).TotalSeconds < (SearchingFast ? 3 : 30)) return null;
                wireSockSearched = now;
                wireSockFound = WireSockCandidates.FirstOrDefault(File.Exists);
                if (wireSockFound == null)
                {
                    foreach (string dir in WireSockDirs())
                    {
                        try { wireSockFound = Directory.GetFiles(dir, "wiresock-client.exe", SearchOption.AllDirectories).FirstOrDefault(); }
                        catch { }
                        if (wireSockFound != null) break;
                    }
                }
                if (wireSockFound != null) wireSockVerified = now;
                return wireSockFound;
            }
        }

        // Folders where a WireSock package may live: Program Files\WireSock* and installer records
        static IEnumerable<string> WireSockDirs()
        {
            var dirs = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                try { dirs.AddRange(Directory.GetDirectories(Environment.GetFolderPath(folder), "WireSock*")); } catch { }
            }
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view))
                    using (var uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        foreach (string sub in uninstall.GetSubKeyNames())
                        {
                            using (var k = uninstall.OpenSubKey(sub))
                            {
                                string name = k.GetValue("DisplayName") as string;
                                string location = k.GetValue("InstallLocation") as string;
                                if (name != null && name.IndexOf("WireSock", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                    !string.IsNullOrEmpty(location) && Directory.Exists(location))
                                    dirs.Add(location.TrimEnd('\\'));
                            }
                        }
                    }
                }
                catch { }
            }
            return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // WireSock's own Windows service (used by its UI app) is connected
        public static bool WireSockServiceRunning
        {
            get
            {
                System.ServiceProcess.ServiceController[] services = null;
                try
                {
                    services = System.ServiceProcess.ServiceController.GetServices();
                    foreach (var s in services)
                    {
                        if (s.ServiceName.StartsWith("wiresock-", StringComparison.OrdinalIgnoreCase) &&
                            s.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                            return true;
                    }
                    return false;
                }
                catch { return false; }
                finally
                {
                    if (services != null)
                        foreach (var s in services) { try { s.Dispose(); } catch { } }
                }
            }
        }

        public static void StopWireSockService()
        {
            System.ServiceProcess.ServiceController[] services = System.ServiceProcess.ServiceController.GetServices();
            try
            {
                foreach (var s in services)
                {
                    if (!s.ServiceName.StartsWith("wiresock-", StringComparison.OrdinalIgnoreCase) ||
                        s.Status != System.ServiceProcess.ServiceControllerStatus.Running) continue;
                    s.Stop();
                    s.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
            }
            finally
            {
                foreach (var s in services) { try { s.Dispose(); } catch { } }
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

        // The user's Downloads folder (it can be moved, so ask Windows)
        public static string Downloads
        {
            get
            {
                IntPtr p;
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) != 0) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        // The folder listing is cached: the app's own writes and the configs watcher call ConfigsChanged
        static string[] configNames;
        static int configsVersion;

        // Bumped on every change to the configs folder (lets the tray menu rebuild only when needed)
        public static int ConfigsVersion { get { return configsVersion; } }

        public static void ConfigsChanged()
        {
            configNames = null;
            configsVersion++;
        }

        // Sorted names of the configs; the array is shared, so callers must not modify it
        public static string[] ConfigNames()
        {
            if (configNames == null)
            {
                configNames = Directory.GetFiles(Configs, "*.conf").Where(IsConf)
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            return configNames;
        }
    }

    class Settings
    {
        public string Config;
        public bool NetworkLock;
        public bool AutoConnect;
        public bool AutoReconnect = true;
        public bool OptimizeMtu = true;
        public bool SkipUac = true;
        public bool SharedApps = true;
        public AppMode AppsMode = AppMode.All;
        public List<string> Apps = new List<string>();
        public bool HasSharedAppsKey;
        public bool Hotkey = false;
        public bool CheckUpdates = true;
        public string AccentColor = "green";
        public string InstalledDigest = "";
        public double WindowLeft = double.NaN;
        public double WindowTop = double.NaN;
        public List<string> Recent = new List<string>(); // servers connected to, newest first

        const int MaxRecent = 5;

        public void AddRecent(string name)
        {
            if (name == null) return;
            Recent.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, name);
            if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }

        public void RenameRecent(string oldName, string newName)
        {
            int i = Recent.FindIndex(n => n.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Recent[i] = newName;
        }

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
                if (d.TryGetValue("optimizeMtu", out v) && v is bool) s.OptimizeMtu = (bool)v;
                if (d.TryGetValue("skipUac", out v) && v is bool) s.SkipUac = (bool)v;
                if (d.TryGetValue("hotkey", out v) && v is bool) s.Hotkey = (bool)v;
                if (d.TryGetValue("checkUpdates", out v) && v is bool) s.CheckUpdates = (bool)v;
                if (d.TryGetValue("accentColor", out v) && v is string) s.AccentColor = (string)v;
                if (d.TryGetValue("installedDigest", out v) && v is string) s.InstalledDigest = (string)v;
                if (d.TryGetValue("windowLeft", out v) && (v is int || v is decimal || v is double)) s.WindowLeft = Convert.ToDouble(v);
                if (d.TryGetValue("windowTop", out v) && (v is int || v is decimal || v is double)) s.WindowTop = Convert.ToDouble(v);
                if (d.TryGetValue("sharedApps", out v) && v is bool) { s.SharedApps = (bool)v; s.HasSharedAppsKey = true; }
                if (d.TryGetValue("appsMode", out v) && v is string)
                {
                    AppMode m;
                    if (Enum.TryParse((string)v, true, out m)) s.AppsMode = m;
                }
                var apps = d.TryGetValue("apps", out v) ? v as System.Collections.IEnumerable : null;
                if (apps != null && !(apps is string))
                {
                    foreach (object o in apps)
                    {
                        string a = o as string;
                        if (!string.IsNullOrEmpty(a) && !s.Apps.Contains(a, StringComparer.OrdinalIgnoreCase)) s.Apps.Add(a);
                    }
                }
                var list = d.TryGetValue("recent", out v) ? v as System.Collections.IEnumerable : null;
                if (list != null && !(list is string))
                {
                    foreach (object o in list)
                    {
                        string n = o as string;
                        if (!string.IsNullOrEmpty(n) && s.Recent.Count < MaxRecent &&
                            !s.Recent.Contains(n, StringComparer.OrdinalIgnoreCase)) s.Recent.Add(n);
                    }
                }
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
                    { "autoReconnect", AutoReconnect }, { "optimizeMtu", OptimizeMtu }, { "skipUac", SkipUac },
                    { "recent", Recent }, { "sharedApps", SharedApps }, { "appsMode", AppsMode.ToString() },
                    { "apps", Apps }, { "hotkey", Hotkey }, { "checkUpdates", CheckUpdates },
                    { "accentColor", AccentColor ?? "green" },
                    { "installedDigest", InstalledDigest ?? "" },
                };
                if (!double.IsNaN(WindowLeft) && !double.IsNaN(WindowTop))
                {
                    d["windowLeft"] = Math.Round(WindowLeft);
                    d["windowTop"] = Math.Round(WindowTop);
                }
                File.WriteAllText(Paths.Settings, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }
    }

    enum AppMode { All, Only, Except }

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

        // Normalizes the config into Paths.Runtime before launching WireSock:
        // - All modes: ensures PersistentKeepalive = 25 in [Peer] when absent or 0 (prevents NAT timeout drops)
        //   and strips legacy inline "# vpn-panel-mtu" comments from MTU lines.
        // - Split-tunneling modes (Only / Except):
        //   1. Strips IPv6 addresses/routes (even IPv6-only lines) so WireSock does not bind IPv6/::0 onto the physical NIC.
        //   2. Comments out [Interface] DNS = ... so Windows Dnscache (svchost.exe) stays on local DNS instead of stalling.
        //   3. Ensures DisallowedIPs excludes loopback/LAN/multicast from the tunnel.
        public static string EffectivePath(string name, out bool ipv6Stripped)
        {
            ipv6Stripped = false;
            string src = Paths.Conf(name);
            ConfigInfo info = ConfigInfo.Read(name);
            if (!info.Readable) return src;
            bool splitMode = info.Mode != AppMode.All;
            try
            {
                string[] rawLines = File.ReadAllLines(src);
                var lines = new List<string>(rawLines.Length + 4);
                bool anyChanged = false, inInterface = false, inPeer = false;
                bool hasDisallowedIps = false, hasKeepalive = false, keepaliveInserted = false;
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
                        if (inPeer && peerCount == 1 && !hasKeepalive && !keepaliveInserted)
                        {
                            lines.Add("PersistentKeepalive = 25");
                            keepaliveInserted = true;
                            anyChanged = true;
                        }
                        inInterface = IsInterface(sec);
                        inPeer = IsPeer(sec);
                        if (inPeer) peerCount++;
                        lines.Add(line);
                        continue;
                    }
                    if (inInterface && MtuLine.IsMatch(line) && line.IndexOf(MtuMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int idx = line.IndexOf('#');
                        if (idx > 0)
                        {
                            line = line.Substring(0, idx).TrimEnd();
                            anyChanged = true;
                        }
                    }
                    if (inPeer && peerCount == 1)
                    {
                        Match km = KeepaliveLine.Match(line);
                        if (km.Success)
                        {
                            int ka;
                            if (!int.TryParse(km.Groups[1].Value, out ka) || ka <= 0)
                            {
                                line = "PersistentKeepalive = 25";
                                anyChanged = true;
                            }
                            keepaliveInserted = true;
                        }
                    }
                    if (splitMode)
                    {
                        if (inInterface && DnsLine.IsMatch(line))
                        {
                            lines.Add("# " + line.Trim() + " # vpn-panel: DNS local mantido em split tunneling");
                            anyChanged = true;
                            continue;
                        }
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
                if (inPeer && peerCount == 1 && !hasKeepalive && !keepaliveInserted)
                {
                    lines.Add("PersistentKeepalive = 25");
                    anyChanged = true;
                }
                if (splitMode && inPeer && !hasDisallowedIps)
                {
                    lines.Add("DisallowedIPs = " + BuildDisallowedIps(tunnelIpv4));
                    anyChanged = true;
                }
                if (!splitMode && !anyChanged) return src;
                Directory.CreateDirectory(Paths.Runtime);
                string dst = Path.Combine(Paths.Runtime, name + ".conf");
                File.WriteAllLines(dst, lines.ToArray(), new UTF8Encoding(false));
                ipv6Stripped = splitMode;
                return dst;
            }
            catch { return src; }
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
                   d.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0;
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
                HashSet<string> addrs, gws, prefixes;
                CollectVpnIpv6(out addrs, out gws, out prefixes);
                bool tunnelUp = Tunnel.Active(false);
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (!tunnelUp && IsWireSockAdapter(ni) && ni.OperationalStatus == OperationalStatus.Up)
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
            }
            catch { }
            return false;
        }

        static readonly Regex DefaultV6RouteLine = new Regex(@"(?:^|\s)::/0\s+(\d+)\s+(\S+)", RegexOptions.Compiled);

        // Purges any lingering VPN IPv6 addresses/routes stuck on physical NICs (from v2.6 or full-tunnel runs)
        // and flushes the Windows DNS resolver cache so negative cache entries (0.0.0.0) don't stall Steam/games.
        public static void CleanGhostNetwork()
        {
            try { DnsFlushResolverCache(); } catch { }
            try
            {
                HashSet<string> addrs, gws, prefixes;
                CollectVpnIpv6(out addrs, out gws, out prefixes);
                var netshCmds = new List<string>();
                var taintedIfs = new HashSet<int>();
                var wireSockIfs = new HashSet<int>();
                bool tunnelUp = Tunnel.Active(false);

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
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

                    bool isWs = !tunnelUp && IsWireSockAdapter(ni);
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

                // Inspect active IPv6 routing table so ghost ::/0 routes with non-:1 gateways are also purged
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

        static dynamic Get(dynamic folder, string name)
        {
            try { return folder.GetTask(name); } catch { return null; }
        }

        static dynamic Get(string name)
        {
            try { return Get(Folder(), name); } catch { return null; }
        }

        static string CommandOf(dynamic folder, string name)
        {
            try
            {
                dynamic task = Get(folder, name);
                if (task == null) return null;
                foreach (dynamic action in task.Definition.Actions) return (string)action.Path;
            }
            catch { }
            return null;
        }

        static bool PointsHere(dynamic folder, string name)
        {
            string cmd = CommandOf(folder, name);
            return cmd != null && cmd.Equals(Paths.Exe, StringComparison.OrdinalIgnoreCase);
        }

        public static bool PointsHere(string name)
        {
            try { return PointsHere(Folder(), name); } catch { return false; }
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

        static void Create(dynamic folder, string name)
        {
            if (!Paths.IsInstalled) return;
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
            folder.RegisterTask(name, xml, 6, null, null, 3, null);
        }

        public static void Create(string name)
        {
            if (!Paths.IsInstalled) return;
            Create(Folder(), name);
        }

        static void Delete(dynamic folder, string name)
        {
            if (Get(folder, name) != null) folder.DeleteTask(name, 0);
        }

        public static void Delete(string name)
        {
            Delete(Folder(), name);
        }

        // Re-point existing tasks at this exe if it was moved, and add/remove the "open" task.
        // Returns whether the Logon task exists, using a single COM connection.
        public static bool Sync(bool skipUac)
        {
            try
            {
                dynamic folder = Folder();
                bool logonExists = Get(folder, Logon) != null;
                if (Paths.IsInstalled)
                {
                    if (logonExists && !PointsHere(folder, Logon)) Create(folder, Logon);
                    if (skipUac && !PointsHere(folder, Open)) Create(folder, Open);
                    if (!skipUac) Delete(folder, Open);
                }
                return logonExists;
            }
            catch { return false; }
        }
    }

    static class Updater
    {
        public class ReleaseInfo
        {
            public string Version;
            public string HtmlUrl;
            public string DownloadUrl;
            public string Notes;
            public string SetupDigest;
            public string AppDigest;
            public bool IsNewer;
            public bool SameVersionUpdated;
        }

        const string ApiUrl = "https://api.github.com/repos/JMqz/VPN-Panel/releases/latest";

        static Version ParseVersion(string ver)
        {
            if (string.IsNullOrEmpty(ver)) return null;
            string clean = Regex.Replace(ver.Trim(), @"^[^\d]*", "");
            if (string.IsNullOrEmpty(clean)) return null;
            try
            {
                return new Version(clean.Contains(".") ? clean : clean + ".0");
            }
            catch
            {
                return null;
            }
        }

        public static bool IsNewer(string remoteTag, string currentVer)
        {
            Version r = ParseVersion(remoteTag);
            Version c = ParseVersion(currentVer);
            return r != null && c != null && r > c;
        }

        public static bool IsSameVersion(string remoteTag, string currentVer)
        {
            Version r = ParseVersion(remoteTag);
            Version c = ParseVersion(currentVer);
            return r != null && c != null && r == c;
        }

        public static string FileSha256(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder("sha256:", 7 + hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        static string cachedLocalExeDigest;

        public static void Check(Action<ReleaseInfo, Exception> callback)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // Tls12 + Tls13
                    var req = (HttpWebRequest)WebRequest.Create(ApiUrl + "?t=" + DateTime.UtcNow.Ticks);
                    req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                    req.Headers["Cache-Control"] = "no-cache, no-store";
                    req.Headers["Pragma"] = "no-cache";
                    req.UserAgent = "vpn-panel";
                    req.Timeout = 10000;
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                        string tag = d.ContainsKey("tag_name") ? Convert.ToString(d["tag_name"]) : "";
                        string html = d.ContainsKey("html_url") ? Convert.ToString(d["html_url"]) : "";
                        string body = d.ContainsKey("body") ? Convert.ToString(d["body"]) : "";
                        string downloadUrl = null;
                        string setupDigest = null;
                        string appDigest = null;
                        var allAppDigests = new List<string>();

                        object assetsObj;
                        if (d.TryGetValue("assets", out assetsObj) && assetsObj is System.Collections.IEnumerable)
                        {
                            foreach (object item in (System.Collections.IEnumerable)assetsObj)
                            {
                                var asset = item as Dictionary<string, object>;
                                if (asset != null && asset.ContainsKey("name") && asset.ContainsKey("browser_download_url"))
                                {
                                    string name = Convert.ToString(asset["name"]);
                                    string url = Convert.ToString(asset["browser_download_url"]);
                                    string digest = asset.ContainsKey("digest") && asset["digest"] != null ? Convert.ToString(asset["digest"]) : null;
                                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                                    if (name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (downloadUrl == null || name.StartsWith("VPN-Panel-Setup-", StringComparison.OrdinalIgnoreCase))
                                        {
                                            downloadUrl = url;
                                            setupDigest = digest;
                                        }
                                    }
                                    else
                                    {
                                        if (!string.IsNullOrEmpty(digest)) allAppDigests.Add(digest);
                                        if (appDigest == null || name.IndexOf("portatil", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            appDigest = digest;
                                        }
                                    }
                                }
                            }
                        }

                        bool versionNewer = IsNewer(tag, Program.Version);
                        bool sameVerUpdated = false;
                        if (!versionNewer && IsSameVersion(tag, Program.Version) && (!string.IsNullOrEmpty(appDigest) || !string.IsNullOrEmpty(setupDigest)))
                        {
                            if (cachedLocalExeDigest == null) cachedLocalExeDigest = FileSha256(Paths.Exe);
                            string localDigest = cachedLocalExeDigest;
                            string savedDigest = Settings.Load().InstalledDigest;
                            bool matchesLocalExe = !string.IsNullOrEmpty(localDigest) &&
                                (!string.IsNullOrEmpty(appDigest)
                                    ? string.Equals(localDigest, appDigest, StringComparison.OrdinalIgnoreCase)
                                    : allAppDigests.Any(x => string.Equals(x, localDigest, StringComparison.OrdinalIgnoreCase)));
                            bool matchesSavedSetup = !string.IsNullOrEmpty(savedDigest) &&
                                ((!string.IsNullOrEmpty(setupDigest) && string.Equals(savedDigest, setupDigest, StringComparison.OrdinalIgnoreCase)) ||
                                 (!string.IsNullOrEmpty(appDigest) && string.Equals(savedDigest, appDigest, StringComparison.OrdinalIgnoreCase)));

                            if (!matchesLocalExe && !matchesSavedSetup)
                            {
                                sameVerUpdated = true;
                            }
                        }

                        var info = new ReleaseInfo
                        {
                            Version = Regex.Replace(tag, @"^[^\d]*", ""),
                            HtmlUrl = html,
                            DownloadUrl = downloadUrl,
                            Notes = body,
                            SetupDigest = setupDigest,
                            AppDigest = appDigest,
                            IsNewer = versionNewer || sameVerUpdated,
                            SameVersionUpdated = sameVerUpdated
                        };

                        var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                        if (callback != null)
                        {
                            if (ui != null && !ui.CheckAccess()) ui.BeginInvoke(new Action(() => callback(info, null)));
                            else callback(info, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (callback != null)
                    {
                        if (ui != null && !ui.CheckAccess()) ui.BeginInvoke(new Action(() => callback(null, ex)));
                        else callback(null, ex);
                    }
                }
            });
        }
    }

    // Country/city of server endpoints (ipinfo.io), cached on disk so cards show it instantly
    static class Geo
    {
        public class Place
        {
            public string Country, City;
            string countryName;

            public string CountryName
            {
                get
                {
                    if (countryName == null)
                    {
                        try { countryName = new RegionInfo(Country).DisplayName; }
                        catch { countryName = Country ?? ""; }
                    }
                    return countryName;
                }
            }
        }

        const int MaxConcurrent = 3;
        static Dictionary<string, Place> cache;
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> failed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<Action> queue = new Queue<Action>();
        static int activeLookups;
        static bool dirty;
        static int version;

        public static int Version { get { return version; } }

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
            if (!dirty || cache == null) return;
            try
            {
                var d = cache.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object> { { "country", kv.Value.Country }, { "city", kv.Value.City } });
                File.WriteAllText(Paths.GeoCache, new JavaScriptSerializer().Serialize(d));
                dirty = false;
            }
            catch { }
        }

        public static void Flush()
        {
            Save();
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
            if (cache.ContainsKey(host)) return;
            DateTime failTime;
            if (failed.TryGetValue(host, out failTime) && (DateTime.UtcNow - failTime).TotalMinutes < 10) return;
            if (!pending.Add(host)) return;

            var ui = Application.Current != null ? Application.Current.Dispatcher : System.Windows.Threading.Dispatcher.CurrentDispatcher;
            queue.Enqueue(() =>
            {
                Place place = null;
                try
                {
                    IPAddress ip;
                    if (!IPAddress.TryParse(host, out ip)) ip = Dns.GetHostAddresses(host).First();
                    var req = (HttpWebRequest)WebRequest.Create("https://ipinfo.io/" + ip + "/json");
                    req.UserAgent = "vpn-panel";
                    req.Timeout = 6000;
                    req.ReadWriteTimeout = 6000;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
                        object country, city;
                        if (j.TryGetValue("country", out country))
                            place = new Place { Country = Convert.ToString(country), City = j.TryGetValue("city", out city) ? Convert.ToString(city) : "" };
                    }
                }
                catch { }
                ui.BeginInvoke(new Action(() =>
                {
                    pending.Remove(host);
                    activeLookups--;
                    PumpQueue();
                    if (place == null)
                    {
                        failed[host] = DateTime.UtcNow;
                        if (activeLookups == 0 && queue.Count == 0) Save();
                        return;
                    }
                    failed.Remove(host);
                    cache[host] = place;
                    dirty = true;
                    version++;
                    if (activeLookups == 0 && queue.Count == 0) Save();
                    onFound();
                }));
            });
            PumpQueue();
        }

        static void PumpQueue()
        {
            while (activeLookups < MaxConcurrent && queue.Count > 0)
            {
                Action work = queue.Dequeue();
                activeLookups++;
                ThreadPool.QueueUserWorkItem(_ => work());
            }
        }
    }

    // Ping (ICMP) to server endpoints. Only measured on demand while the Servidores tab is visible,
    // kept in memory only (latency from an earlier session or network says little about now).
    static class Latency
    {
        public class Result
        {
            public int Ms = -1;         // -1: no ICMP reply (many WireGuard servers filter ping)
            public bool ViaTunnel;      // measured while a tunnel was up: other servers' traffic goes through the VPN
            public DateTime At;
            public bool Ok { get { return Ms >= 0; } }
        }

        const int MaxConcurrent = 4;
        const int FirstTimeoutMs = 1500, SecondTimeoutMs = 1000;
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

        static readonly Dictionary<string, Result> cache = new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<KeyValuePair<string, Action>> queue = new Queue<KeyValuePair<string, Action>>();
        static int active, generation;

        // Fired on the UI thread with the host whose result arrived
        public static event Action<string> Measured;

        public static bool IsPending(string host) { return host != null && pending.Contains(host); }

        public static Result Cached(string host)
        {
            Result r;
            return host != null && cache.TryGetValue(host, out r) ? r : null;
        }

        // Queues each host that has no result younger than MaxAge (all of them when force); duplicates are ignored
        public static void Measure(IEnumerable<string> hosts, bool force, bool viaTunnel)
        {
            var ui = Application.Current != null ? Application.Current.Dispatcher : System.Windows.Threading.Dispatcher.CurrentDispatcher;
            foreach (string host in hosts.Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Result old = Cached(host);
                if (!force && old != null && DateTime.UtcNow - old.At < MaxAge) continue;
                if (force) cache.Remove(host);
                if (!pending.Add(host)) continue;

                string h = host;
                int gen = generation;
                queue.Enqueue(new KeyValuePair<string, Action>(h, () =>
                {
                    int ms = Probe(h);
                    ui.BeginInvoke(new Action(() =>
                    {
                        active--;
                        // routing changed meanwhile (connected/disconnected): this number no longer applies
                        if (gen == generation)
                        {
                            pending.Remove(h);
                            cache[h] = new Result { Ms = ms, ViaTunnel = viaTunnel, At = DateTime.UtcNow };
                            if (Measured != null) Measured(h);
                        }
                        Pump();
                    }));
                }));
            }
            Pump();
        }

        // Window hidden: drop what hasn't started yet (the few running probes end within ~2.5 s)
        public static void CancelQueued()
        {
            while (queue.Count > 0) pending.Remove(queue.Dequeue().Key);
        }

        // Connected or disconnected: the path to every server changed, so old numbers are discarded
        public static void RouteChanged()
        {
            generation++;
            cache.Clear();
            CancelQueued();
            pending.Clear();
        }

        static void Pump()
        {
            while (active < MaxConcurrent && queue.Count > 0)
            {
                Action work = queue.Dequeue().Value;
                active++;
                ThreadPool.QueueUserWorkItem(_ => work());
            }
        }

        // Best of two echoes (the first can include ARP/route setup); a host that ignores the first isn't asked again
        static int Probe(string host)
        {
            try
            {
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip))
                {
                    IPAddress[] addrs = null;
                    Exception dnsEx = null;
                    var done = new ManualResetEventSlim(false);
                    Dns.BeginGetHostAddresses(host, ar =>
                    {
                        try { addrs = Dns.EndGetHostAddresses(ar); }
                        catch (Exception ex) { dnsEx = ex; }
                        finally { try { done.Set(); } catch { } }
                    }, null);
                    bool signaled = done.Wait(1500);
                    done.Dispose();
                    if (!signaled || dnsEx != null || addrs == null || addrs.Length == 0) return -1;
                    ip = addrs[0];
                }
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var first = ping.Send(ip, FirstTimeoutMs);
                    if (first.Status != System.Net.NetworkInformation.IPStatus.Success) return -1;
                    long best = first.RoundtripTime;
                    var second = ping.Send(ip, SecondTimeoutMs);
                    if (second.Status == System.Net.NetworkInformation.IPStatus.Success) best = Math.Min(best, second.RoundtripTime);
                    return (int)Math.Max(1, best);
                }
            }
            catch { return -1; }
        }
    }

    // Bytes through the physical network card: wiresock-client (no -lac) filters it directly, there's no virtual adapter to read.
    // The kernel counters are cumulative, so a baseline taken on connect gives the session total even after hours in the tray
    // with no calls at all. Sampling allocates nothing: one unmanaged MIB_IF_ROW2 buffer, read in place.
    static class Traffic
    {
        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
        [DllImport("iphlpapi.dll")]
        static extern int GetIfEntry2(IntPtr row);

        // MIB_IF_ROW2 is 1352 bytes on x86 and x64; only these fields are touched
        const int RowSize = 1352, OffLuid = 0, OffIndex = 8, OffInOctets = 1208, OffOutOctets = 1280;
        const uint FallbackDest = 0x01010101; // 1.1.1.1 when the endpoint is a name or IPv6 (no DNS here)

        static IntPtr row;
        static uint dest, ifIndex;
        static ulong baseIn, baseOut, lastIn, lastOut, carriedIn, carriedOut;
        static long lastTicks;

        public static bool Running { get; private set; }
        public static ulong SessionIn { get { return carriedIn + (lastIn - baseIn); } }
        public static ulong SessionOut { get { return carriedOut + (lastOut - baseOut); } }
        public static double InRate { get; private set; }  // bytes/s, -1 until two samples close together
        public static double OutRate { get; private set; }

        // Connected (first handshake): the route to the server carries the encapsulated traffic
        public static void Start(string endpoint)
        {
            IPAddress ip;
            dest = endpoint != null && IPAddress.TryParse(endpoint, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? BitConverter.ToUInt32(ip.GetAddressBytes(), 0) : FallbackDest;
            carriedIn = carriedOut = 0;
            ifIndex = 0;
            Running = true;
            InRate = OutRate = 0;
            lastTicks = 0;
            ulong i, o;
            if (Read(out i, out o)) { baseIn = lastIn = i; baseOut = lastOut = o; lastTicks = Stopwatch.GetTimestamp(); }
            else baseIn = lastIn = baseOut = lastOut = 0;
        }

        public static void Stop() { Running = false; }

        // Call about once a second while someone is looking; false when the counters can't be read
        public static bool Sample()
        {
            if (!Running) return false;
            uint before = ifIndex;
            ulong i, o;
            if (!Read(out i, out o)) return false;
            long now = Stopwatch.GetTimestamp();
            // another card (Wi-Fi -> cable), a reset adapter or a baseline that failed: keep what was counted, restart from here
            if (ifIndex != before || lastTicks == 0 || i < lastIn || o < lastOut)
            {
                if (lastTicks != 0) { carriedIn += lastIn - baseIn; carriedOut += lastOut - baseOut; }
                baseIn = lastIn = i; baseOut = lastOut = o;
                lastTicks = now;
                InRate = OutRate = -1;
                return true;
            }
            double dt = (now - lastTicks) / (double)Stopwatch.Frequency;
            if (dt < 0.5) return true; // too close to the previous sample (page switch right before a tick)
            if (dt > 3) InRate = OutRate = -1; // back from the tray or another tab: a long average isn't "now"
            else
            {
                double inNow = (i - lastIn) / dt, outNow = (o - lastOut) / dt;
                InRate = InRate < 0 ? inNow : InRate * 0.4 + inNow * 0.6;
                OutRate = OutRate < 0 ? outNow : OutRate * 0.4 + outNow * 0.6;
            }
            lastIn = i; lastOut = o; lastTicks = now;
            return true;
        }

        static bool Read(out ulong inOctets, out ulong outOctets)
        {
            inOctets = outOctets = 0;
            uint idx;
            if (GetBestInterface(dest, out idx) != 0 || idx == 0) return false;
            ifIndex = idx;
            if (row == IntPtr.Zero) row = Marshal.AllocHGlobal(RowSize); // kept for the life of the process
            Marshal.WriteInt64(row, OffLuid, 0); // zero LUID: GetIfEntry2 looks the row up by index
            Marshal.WriteInt32(row, OffIndex, (int)idx);
            if (GetIfEntry2(row) != 0) return false;
            inOctets = (ulong)Marshal.ReadInt64(row, OffInOctets);
            outOctets = (ulong)Marshal.ReadInt64(row, OffOutOctets);
            return true;
        }
    }

    // The wiresock-client process
    static class Tunnel
    {
        static readonly object stateLock = new object();
        static readonly object stopLock = new object();
        static int stoppingCount;
        static int currentPid;
        static IntPtr currentHandle;
        static bool lockHint; // the tracked tunnel runs with -network-lock
        public static string LastStartedConfig { get; private set; }
        public static DateTime LastStartedAt { get; private set; }
        public static bool Ipv6Isolated { get; private set; }
        public static int Pid { get { lock (stateLock) return currentPid; } }
        public static DateTime StartTime { get; private set; }
        public static bool IsStopping { get { return Interlocked.CompareExchange(ref stoppingCount, 0, 0) > 0; } }
        public static bool HasPendingNetworkLock
        {
            get { return lockHint || File.Exists(Paths.NetworkLockFlag); }
        }

        static void WriteNetworkLockFlag(string config)
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                File.WriteAllText(Paths.NetworkLockFlag, config ?? "");
            }
            catch { }
        }

        static void ClearNetworkLockFlag()
        {
            try { if (File.Exists(Paths.NetworkLockFlag)) File.Delete(Paths.NetworkLockFlag); }
            catch { }
        }

        public static Process[] Find() { return Process.GetProcessesByName("wiresock-client"); }

        static void ReleaseCurrent()
        {
            lock (stateLock)
            {
                ProcUtil.Close(ref currentHandle);
                currentPid = 0;
            }
        }

        static void Track(int pid, IntPtr handle)
        {
            lock (stateLock)
            {
                ProcUtil.Close(ref currentHandle);
                currentPid = pid;
                currentHandle = handle;
                StartTime = ProcUtil.StartTime(handle);
            }
        }

        // True while the tracked client lives, avoiding a full system process scan on every tick.
        // Holding currentHandle prevents Windows from reusing the PID while open.
        public static bool Active(bool allowScan)
        {
            if (Interlocked.CompareExchange(ref stoppingCount, 0, 0) > 0) return false;
            lock (stateLock)
            {
                if (currentPid != 0)
                {
                    if (ProcUtil.IsAlive(currentHandle)) return true;
                    ProcUtil.Close(ref currentHandle);
                    currentPid = 0;
                }
            }
            if (!allowScan) return false;
            bool found = false; // running but not openable: reported now, scanned again next tick
            foreach (Process p in Find())
            {
                found = true;
                if (Pid == 0)
                {
                    IntPtr h = ProcUtil.OpenForMonitor(p.Id);
                    if (h != IntPtr.Zero) Track(p.Id, h);
                }
                p.Dispose();
            }
            lock (stateLock)
            {
                if (found && currentPid == 0) StartTime = DateTime.Now;
            }
            return found;
        }

        // Called with the command line of a tunnel this instance did not start
        public static void NoteCommandLine(string commandLine)
        {
            lockHint = commandLine != null && Regex.IsMatch(commandLine, "-network-lock\\s+(enabled|on)\\b", RegexOptions.IgnoreCase);
            string cfg = ConfigOf(commandLine);
            ConfigInfo info = cfg != null ? ConfigInfo.Read(cfg) : null;
            Ipv6Isolated = commandLine != null &&
                           commandLine.IndexOf(Paths.Runtime, StringComparison.OrdinalIgnoreCase) >= 0 &&
                           (info == null || info.Mode != AppMode.All);
        }

        public static string CommandLine(int pid)
        {
            string cmd = ProcUtil.CommandLine(pid);
            if (!string.IsNullOrEmpty(cmd)) return cmd;
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
            lock (stopLock)
            {
                // Never leave an orphaned client fighting for the WireSock driver
                foreach (Process existing in Find())
                {
                    ProcUtil.Shutdown(existing.Id, 2000);
                    existing.Dispose();
                }

                ConfigInfo info = ConfigInfo.Read(config);
                // Kill switch blocks svchost.exe (local DNS) and non-tunneled apps in split-tunneling modes
                if (info != null && info.Mode != AppMode.All) networkLock = false;

                LastStartedConfig = config;
                LastStartedAt = DateTime.UtcNow;
                bool v6Stripped;
                string confPath = ConfFile.EffectivePath(config, out v6Stripped);
                Ipv6Isolated = v6Stripped;
                if (v6Stripped && ConfFile.HasGhostIpv6()) ConfFile.CleanGhostNetwork();
                if (networkLock) WriteNetworkLockFlag(config);
                else ClearNetworkLockFlag();
                // "info" logs handshakes, which is how the app knows the server really answered
                string cmdLine = "\"" + exe + "\" run -config \"" + confPath + "\" -log-level info";
                if (networkLock) cmdLine += " -network-lock enabled";
                // The client writes straight to the log file it inherits, so it keeps running (and logging)
                // after the panel exits, and any later instance can follow the same file
                int pid;
                try
                {
                    IntPtr h = ProcUtil.StartLogged(exe, cmdLine, Paths.Log, out pid);
                    Track(pid, h);
                    lockHint = networkLock;
                }
                catch
                {
                    if (networkLock) ClearNetworkLockFlag();
                    throw;
                }
            }
        }

        // resetLock: the kill switch may be on. A killed client leaves the driver blocking all traffic,
        // so the lock is reset once every client is gone (harmless when no lock is active).
        public static void Stop(bool resetLock)
        {
            Interlocked.Increment(ref stoppingCount);
            try
            {
                lock (stopLock)
                {
                    LastStartedConfig = null;
                    Ipv6Isolated = false;
                    resetLock |= lockHint || File.Exists(Paths.NetworkLockFlag);
                    lockHint = false;
                    int pidToStop;
                    lock (stateLock) pidToStop = currentPid;
                    ReleaseCurrent();
                    bool anyFound = pidToStop != 0;
                    bool stoppedCleanly = true;
                    // Ctrl+C first: WireSock then drops the tunnel and releases the lock by itself
                    if (pidToStop != 0) stoppedCleanly = ProcUtil.Shutdown(pidToStop, 3000);
                    foreach (Process p in Find())
                    {
                        anyFound = true;
                        stoppedCleanly = ProcUtil.Shutdown(p.Id, 3000) && stoppedCleanly;
                        p.Dispose();
                    }
                    if (!anyFound) stoppedCleanly = false;
                    ConfFile.DeleteRuntimeCopies();
                    if (ConfFile.HasGhostIpv6()) ConfFile.CleanGhostNetwork();
                    if (resetLock && !stoppedCleanly) ResetNetworkLock();
                    else if (stoppedCleanly) ClearNetworkLockFlag();
                }
            }
            finally
            {
                Interlocked.Decrement(ref stoppingCount);
            }
        }

        public static void ResetNetworkLock()
        {
            lock (stopLock)
            {
                ConfFile.CleanGhostNetwork();
                string exe = Paths.WireSock;
                if (exe != null)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(exe, "reset-network-lock") { UseShellExecute = false, CreateNoWindow = true };
                        using (Process p = Process.Start(psi)) p.WaitForExit(5000);
                    }
                    catch { }
                }
                ClearNetworkLockFlag();
            }
        }
    }

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
    }

    // Fast Win32 process helpers: avoids Process.MainModule exceptions and per-process EnumWindows
    static class ProcUtil
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint SYNCHRONIZE = 0x00100000;
        const uint WAIT_TIMEOUT = 0x00000102;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        const uint PROCESS_TERMINATE = 0x0001;
        const uint WAIT_OBJECT_0 = 0;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_ALL = 0x7; // read | write | delete
        const uint CREATE_ALWAYS = 2;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        const int STARTF_USESTDHANDLES = 0x100;
        const uint CREATE_NO_WINDOW = 0x08000000;
        const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        const int ProcessPowerThrottling = 4;
        const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
        static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
        static readonly IntPtr INVALID_HANDLE_VALUE = (IntPtr)(-1);
        const uint CTRL_C_EVENT = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOEX
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessW(string app, StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr, bool inherit,
            uint flags, IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);

        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr h, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, int processGroupId);

        public static IntPtr OpenForMonitor(int pid)
        {
            return OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        }

        public static DateTime StartTime(IntPtr handle)
        {
            long creation, exit, kernel, user;
            if (handle != IntPtr.Zero && GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                return DateTime.FromFileTime(creation);
            return DateTime.Now;
        }

        // Starts a hidden console program with stdout and stderr going to logPath (truncated), no cmd.exe in between.
        // Only the log handle is inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so the child never holds
        // anything else of the panel's. Returns the process handle (full access); throws Win32Exception.
        public static IntPtr StartLogged(string exe, string cmdLine, string logPath, out int pid)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
            IntPtr log = CreateFileW(logPath, GENERIC_WRITE, FILE_SHARE_ALL, ref sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (log == INVALID_HANDLE_VALUE) log = IntPtr.Zero; // log in use or unwritable: run without one
            IntPtr attrs = IntPtr.Zero, handles = IntPtr.Zero;
            bool attrsReady = false;
            try
            {
                var si = new STARTUPINFOEX { cb = Marshal.SizeOf(typeof(STARTUPINFOEX)) };
                uint flags = CREATE_NO_WINDOW | ABOVE_NORMAL_PRIORITY_CLASS;
                if (log != IntPtr.Zero)
                {
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attrs = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
                    attrsReady = true;
                    handles = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(handles, log);
                    if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception();
                    si.lpAttributeList = attrs;
                    si.dwFlags = STARTF_USESTDHANDLES;
                    si.hStdOutput = si.hStdError = log; // same handle: one file pointer, like 2>&1
                    flags |= EXTENDED_STARTUPINFO_PRESENT;
                }
                PROCESS_INFORMATION pi;
                bool created;
                lock (consoleLock)
                {
                    // The child inherits "ignore Ctrl+C" from us (and we may have inherited it from whoever
                    // launched the panel); clear it so Shutdown can stop the client gracefully later
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                    created = CreateProcessW(exe, new StringBuilder(cmdLine), IntPtr.Zero, IntPtr.Zero, log != IntPtr.Zero,
                        flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi);
                }
                if (!created) throw new Win32Exception();
                CloseHandle(pi.hThread);
                try
                {
                    var pt = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                        StateMask = 0, // disable EcoQoS and timer resolution throttling so UDP packets aren't delayed
                    };
                    SetProcessInformation(pi.hProcess, ProcessPowerThrottling, ref pt, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
                }
                catch { }
                pid = pi.dwProcessId;
                return pi.hProcess;
            }
            finally
            {
                if (attrsReady) DeleteProcThreadAttributeList(attrs);
                if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (log != IntPtr.Zero) CloseHandle(log); // the child keeps its own copy
            }
        }

        static readonly object consoleLock = new object();

        // Ends a console process the way Ctrl+C would, so it can clean up; terminates it after graceMs.
        // Returns true if it exited by itself.
        public static bool Shutdown(int pid, int graceMs)
        {
            IntPtr h = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!IsAlive(h)) return false; // already crashed/exited without graceful Ctrl+C cleanup
                lock (consoleLock)
                {
                    // The panel has no console: borrow the client's for a moment, ignoring the event ourselves
                    // until the client is gone (the ignore flag is inherited, so it can't stay on)
                    if (AttachConsole(pid))
                    {
                        SetConsoleCtrlHandler(IntPtr.Zero, true);
                        bool sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                        FreeConsole();
                        bool exited = sent && WaitForSingleObject(h, (uint)graceMs) == WAIT_OBJECT_0;
                        SetConsoleCtrlHandler(IntPtr.Zero, false);
                        if (exited) return true;
                    }
                }
                TerminateProcess(h, 1);
                WaitForSingleObject(h, 3000);
                return false;
            }
            finally { CloseHandle(h); }
        }

        public static bool IsAlive(IntPtr handle)
        {
            return handle != IntPtr.Zero && WaitForSingleObject(handle, 0) == WAIT_TIMEOUT;
        }

        public static void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        public static string ExePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
            }
            finally { CloseHandle(h); }
        }

        // Fast command line lookup via ProcessCommandLineInformation (60), avoiding WMI overhead
        public static string CommandLine(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint len = 0;
                NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out len);
                if (len == 0 || len > 65536) return null;
                buf = Marshal.AllocHGlobal((int)len);
                if (NtQueryInformationProcess(h, 60, buf, len, out len) != 0) return null;
                var us = (UNICODE_STRING)Marshal.PtrToStructure(buf, typeof(UNICODE_STRING));
                if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
                return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            catch { return null; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }

        public static HashSet<int> WindowedPids()
        {
            var pids = new HashSet<int>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != 0) pids.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
            return pids;
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
                int dark = 1, round = 2, border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE (no window outline)
                DwmSetWindowAttribute(h, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
                DwmSetWindowAttribute(h, 34, ref border, 4);  // DWMWA_BORDER_COLOR
            };
        }
    }
}
