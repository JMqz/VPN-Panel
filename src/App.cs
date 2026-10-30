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
[assembly: AssemblyVersion("2.4.0.0")]
[assembly: AssemblyFileVersion("2.4.0.0")]

namespace VpnPanel
{
    static class Program
    {
        // Same mutex as the old vpn-panel.ps1, so they never run together
        const string MutexName = @"Global\VpnPanelWireSock";
        public const string ShowEventName = @"Global\VpnPanelWireSockShow";
        public const string Version = "2.4";

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

        public static byte[] Bytes(string name)
        {
            using (Stream st = Open(name))
            using (var ms = new MemoryStream()) { st.CopyTo(ms); return ms.ToArray(); }
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
                try
                {
                    return System.ServiceProcess.ServiceController.GetServices().Any(s =>
                        s.ServiceName.StartsWith("wiresock-", StringComparison.OrdinalIgnoreCase) &&
                        s.Status == System.ServiceProcess.ServiceControllerStatus.Running);
                }
                catch { return false; }
            }
        }

        public static void StopWireSockService()
        {
            foreach (var s in System.ServiceProcess.ServiceController.GetServices())
            {
                if (!s.ServiceName.StartsWith("wiresock-", StringComparison.OrdinalIgnoreCase) ||
                    s.Status != System.ServiceProcess.ServiceControllerStatus.Running) continue;
                s.Stop();
                s.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
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
        public bool SkipUac = true;
        public bool SharedApps = true;
        public AppMode AppsMode = AppMode.All;
        public List<string> Apps = new List<string>();
        public bool HasSharedAppsKey;
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
                if (d.TryGetValue("skipUac", out v) && v is bool) s.SkipUac = (bool)v;
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
                    { "autoReconnect", AutoReconnect }, { "skipUac", SkipUac }, { "recent", Recent },
                    { "sharedApps", SharedApps }, { "appsMode", AppsMode.ToString() }, { "apps", Apps },
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
        static readonly Dictionary<string, ConfigInfo> cache = new Dictionary<string, ConfigInfo>(StringComparer.OrdinalIgnoreCase);

        public string Name;
        public string Server;    // first comment in [Peer], e.g. "# US-FREE#40" in Proton configs
        public string Endpoint;  // host only
        public AppMode Mode;
        public List<string> Apps = new List<string>();
        public bool Readable;

        // Every write by the app invalidates its entry, and the configs watcher clears the cache for
        // changes made outside the app, so a cached entry is trusted without touching the disk.
        public static void Invalidate(string name)
        {
            if (name != null) cache.Remove(name);
            Paths.ConfigsChanged();
        }

        public static void InvalidateAll()
        {
            cache.Clear();
            Paths.ConfigsChanged();
        }

        public static ConfigInfo Read(string name)
        {
            ConfigInfo cached;
            if (cache.TryGetValue(name, out cached)) return cached;

            string path = Paths.Conf(name);
            var c = new ConfigInfo { Name = name };
            try
            {
                string[] lines = File.ReadAllLines(path);
                bool inPeer = false;
                foreach (string line in lines)
                {
                    Match m = ConfFile.SectionLine.Match(line);
                    if (m.Success) { inPeer = m.Groups[1].Value.Equals("Peer", StringComparison.OrdinalIgnoreCase); continue; }
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
                            c.Endpoint = ep.StartsWith("[") ? ep.Substring(1, Math.Max(0, ep.IndexOf(']') - 1))
                                       : ep.Contains(':') ? ep.Substring(0, ep.LastIndexOf(':')) : ep;
                        }
                    }
                }
                c.Mode = ConfFile.ReadApps(lines, c.Apps);
                c.Readable = true;
                cache[name] = c;
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
        public static readonly Regex SectionLine = new Regex(@"^\s*\[(\w+)\]\s*$", RegexOptions.Compiled);
        public static readonly Regex CommentLine = new Regex(@"^\s*#\s*(.+?)\s*$", RegexOptions.Compiled);
        public static readonly Regex EndpointLine = new Regex(@"^\s*Endpoint\s*=\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AppsLine = new Regex(@"^\s*(AllowedApps|DisallowedApps)\s*=\s*([^#]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AllowedIpsLine = new Regex(@"^\s*AllowedIPs\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static bool IsPeer(Match section)
        {
            return section.Groups[1].Value.Equals("Peer", StringComparison.OrdinalIgnoreCase);
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

            byte[] body = enc.GetBytes(string.Join(nl, lines) + (trailingNewline ? nl : ""));
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
            try { File.Replace(tmp, path, null); }
            catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }
            lastWriteName = Path.GetFileNameWithoutExtension(path);
            lastWriteAt = DateTime.UtcNow;
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
                            if (IsWireGuard(Decode(ms.ToArray())))
                                found.Add(new KeyValuePair<string, byte[]>(Path.GetFileNameWithoutExtension(entry.Name), ms.ToArray()));
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
                        return;
                    }
                    failed.Remove(host);
                    cache[host] = place;
                    Save();
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
                if (!IPAddress.TryParse(host, out ip)) ip = Dns.GetHostAddresses(host).First();
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
            InRate = OutRate = -1;
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
        static int currentPid;
        static IntPtr currentHandle;
        static bool lockHint; // the tracked tunnel runs with -network-lock
        public static string LastStartedConfig { get; private set; }
        public static DateTime LastStartedAt { get; private set; }
        public static int Pid { get { return currentPid; } }
        public static DateTime StartTime { get; private set; }

        public static Process[] Find() { return Process.GetProcessesByName("wiresock-client"); }

        static void ReleaseCurrent()
        {
            ProcUtil.Close(ref currentHandle);
            currentPid = 0;
        }

        static void Track(int pid, IntPtr handle)
        {
            ReleaseCurrent();
            currentPid = pid;
            currentHandle = handle;
            StartTime = ProcUtil.StartTime(handle);
        }

        // True while the tracked client lives, avoiding a full system process scan on every tick.
        // Holding currentHandle prevents Windows from reusing the PID while open.
        public static bool Active(bool allowScan)
        {
            if (currentPid != 0)
            {
                if (ProcUtil.IsAlive(currentHandle)) return true;
                ReleaseCurrent();
            }
            if (!allowScan) return false;
            bool found = false; // running but not openable: reported now, scanned again next tick
            foreach (Process p in Find())
            {
                found = true;
                if (currentPid == 0)
                {
                    IntPtr h = ProcUtil.OpenForMonitor(p.Id);
                    if (h != IntPtr.Zero) Track(p.Id, h);
                }
                p.Dispose();
            }
            if (found && currentPid == 0) StartTime = DateTime.Now;
            return found;
        }

        // Called with the command line of a tunnel this instance did not start
        public static void NoteCommandLine(string commandLine)
        {
            lockHint = commandLine != null && Regex.IsMatch(commandLine, "-network-lock\\s+(enabled|on)\\b", RegexOptions.IgnoreCase);
        }

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
            LastStartedConfig = config;
            LastStartedAt = DateTime.UtcNow;
            // "info" logs handshakes, which is how the app knows the server really answered
            string cmdLine = "\"" + exe + "\" run -config \"" + Paths.Conf(config) + "\" -log-level info";
            if (networkLock) cmdLine += " -network-lock enabled";
            // The client writes straight to the log file it inherits, so it keeps running (and logging)
            // after the panel exits, and any later instance can follow the same file
            int pid;
            IntPtr h = ProcUtil.StartLogged(exe, cmdLine, Paths.Log, out pid);
            Track(pid, h);
            lockHint = networkLock;
        }

        // resetLock: the kill switch may be on. A killed client leaves the driver blocking all traffic,
        // so the lock is reset once every client is gone (harmless when no lock is active).
        public static void Stop(bool resetLock)
        {
            LastStartedConfig = null;
            resetLock |= lockHint;
            lockHint = false;
            bool stoppedCleanly = false;
            // Ctrl+C first: WireSock then drops the tunnel and releases the lock by itself
            if (currentPid != 0) stoppedCleanly = ProcUtil.Shutdown(currentPid, 3000);
            ReleaseCurrent();
            foreach (Process p in Find())
            {
                stoppedCleanly = ProcUtil.Shutdown(p.Id, 3000) && stoppedCleanly;
                p.Dispose();
            }
            if (resetLock && !stoppedCleanly) ResetNetworkLock();
        }

        static void ResetNetworkLock()
        {
            string exe = Paths.WireSock;
            if (exe == null) return;
            try
            {
                var psi = new ProcessStartInfo(exe, "reset-network-lock") { UseShellExecute = false, CreateNoWindow = true };
                using (Process p = Process.Start(psi)) p.WaitForExit(5000);
            }
            catch { }
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
        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
        static readonly IntPtr INVALID_HANDLE_VALUE = (IntPtr)(-1);
        const uint CTRL_C_EVENT = 0;

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
                uint flags = CREATE_NO_WINDOW;
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
                if (!IsAlive(h)) return true;
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
                int dark = 1, round = 2, border = 0x00352B26; // COLORREF is 0x00BBGGRR
                DwmSetWindowAttribute(h, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
                DwmSetWindowAttribute(h, 34, ref border, 4);  // DWMWA_BORDER_COLOR
            };
        }
    }
}
