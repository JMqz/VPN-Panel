// VPN Panel - Settings (settings.json) and the shared enums.

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
    class Settings
    {
        public string Config;
        public bool NetworkLock;
        public bool AutoConnect;
        public bool AutoReconnect = true;
        public bool AutoHeal = true;
        public bool GameStabilityMode = true;
        public bool LiveGuard = true; // Modo Live: protects Discord calls and streams (see MainWindow.Live.cs)
        public string CustomDns = "";
        public VpnEnginePreference EnginePreference = VpnEnginePreference.Auto;
        public int AdaptiveMtu = 0;
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
        public string SkippedUpdateVersion = "";
        public double WindowLeft = double.NaN;
        public double WindowTop = double.NaN;
        public List<string> Recent = new List<string>(); // servers connected to, newest first
        // Rede de amigos (see Lan.cs / MainWindow.Lan.cs): off by default, nothing is downloaded or run until enabled
        public bool LanEnabled;                 // shows the Rede tab
        public bool LanOn;                      // the user wants the network up (comes back with the app)
        public bool LanProtect = true;          // block file sharing / remote desktop for people in the network
        public string LanNick = "";
        public LanNetwork LanNet;               // the network this PC is in, or null
        public List<string> LanNodes = new List<string>(); // meeting nodes last used, offered when creating a network
        // Not serialized: preserves user's preferred server on disk during temporary auto-failover
        public string PreferredConfigOverride;

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
                if (d.TryGetValue("autoHeal", out v) && v is bool) s.AutoHeal = (bool)v;
                if (d.TryGetValue("gameStabilityMode", out v) && v is bool) s.GameStabilityMode = (bool)v;
                if (d.TryGetValue("liveGuard", out v) && v is bool) s.LiveGuard = (bool)v;
                if (d.TryGetValue("customDns", out v) && v is string)
                {
                    string dns = ((string)v).Trim();
                    IPAddress parsedDns;
                    if (dns.Length == 0 || (IPAddress.TryParse(dns, out parsedDns) && parsedDns.AddressFamily == AddressFamily.InterNetwork))
                        s.CustomDns = dns;
                }
                if (d.TryGetValue("enginePreference", out v) && v is string)
                {
                    VpnEnginePreference ep;
                    if (Enum.TryParse((string)v, true, out ep)) s.EnginePreference = ep;
                }
                if (d.TryGetValue("adaptiveMtu", out v) && (v is int || v is decimal || v is double))
                {
                    int am = Convert.ToInt32(v);
                    if (am == 0 || (am >= 1200 && am <= 1500)) s.AdaptiveMtu = am;
                }
                if (d.TryGetValue("optimizeMtu", out v) && v is bool) s.OptimizeMtu = (bool)v;
                if (d.TryGetValue("skipUac", out v) && v is bool) s.SkipUac = (bool)v;
                if (d.TryGetValue("hotkey", out v) && v is bool) s.Hotkey = (bool)v;
                if (d.TryGetValue("checkUpdates", out v) && v is bool) s.CheckUpdates = (bool)v;
                if (d.TryGetValue("accentColor", out v) && v is string) s.AccentColor = (string)v;
                if (d.TryGetValue("installedDigest", out v) && v is string) s.InstalledDigest = (string)v;
                if (d.TryGetValue("skippedUpdateVersion", out v) && v is string) s.SkippedUpdateVersion = (string)v;
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
                if (d.TryGetValue("lanEnabled", out v) && v is bool) s.LanEnabled = (bool)v;
                if (d.TryGetValue("lanOn", out v) && v is bool) s.LanOn = (bool)v;
                if (d.TryGetValue("lanProtect", out v) && v is bool) s.LanProtect = (bool)v;
                if (d.TryGetValue("lanNick", out v) && v is string) s.LanNick = (string)v;
                if (d.TryGetValue("lanNodes", out v)) s.LanNodes = ReadNodes(v);
                var lanNet = d.TryGetValue("lanNet", out v) ? v as Dictionary<string, object> : null;
                if (lanNet != null)
                {
                    object nv;
                    var net = new LanNetwork
                    {
                        Name = lanNet.TryGetValue("name", out nv) ? nv as string : null,
                        Id = lanNet.TryGetValue("id", out nv) ? nv as string : null,
                        Secret = lanNet.TryGetValue("secret", out nv) ? nv as string : null,
                        Nodes = ReadNodes(lanNet.TryGetValue("nodes", out nv) ? nv : null),
                    };
                    if (!string.IsNullOrEmpty(net.Id) && !string.IsNullOrEmpty(net.Secret) && net.Nodes.Count > 0)
                    {
                        if (string.IsNullOrEmpty(net.Name)) net.Name = net.Id;
                        s.LanNet = net;
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

        static List<string> ReadNodes(object value)
        {
            var nodes = new List<string>();
            var items = value as System.Collections.IEnumerable;
            if (items == null || value is string) return nodes;
            foreach (object o in items)
            {
                string node = Lan.NormalizeNode(o as string);
                if (node != null && nodes.Count < 4 && !nodes.Contains(node)) nodes.Add(node);
            }
            return nodes;
        }

        public void Save()
        {
            try
            {
                string savedCfg = !string.IsNullOrEmpty(PreferredConfigOverride) ? PreferredConfigOverride : Config;
                var d = new Dictionary<string, object>
                {
                    { "config", savedCfg }, { "networkLock", NetworkLock }, { "autoConnect", AutoConnect },
                    { "autoReconnect", AutoReconnect }, { "autoHeal", AutoHeal }, { "gameStabilityMode", GameStabilityMode },
                    { "customDns", CustomDns ?? "" }, { "enginePreference", EnginePreference.ToString() }, { "adaptiveMtu", AdaptiveMtu },
                    { "optimizeMtu", OptimizeMtu }, { "skipUac", SkipUac }, { "liveGuard", LiveGuard },
                    { "recent", Recent }, { "sharedApps", SharedApps }, { "appsMode", AppsMode.ToString() },
                    { "apps", Apps }, { "hotkey", Hotkey }, { "checkUpdates", CheckUpdates },
                    { "accentColor", AccentColor ?? "green" },
                    { "installedDigest", InstalledDigest ?? "" },
                    { "skippedUpdateVersion", SkippedUpdateVersion ?? "" },
                    { "lanEnabled", LanEnabled }, { "lanOn", LanOn }, { "lanProtect", LanProtect },
                    { "lanNick", LanNick ?? "" }, { "lanNodes", LanNodes ?? new List<string>() },
                };
                if (LanNet != null)
                {
                    d["lanNet"] = new Dictionary<string, object>
                    {
                        { "name", LanNet.Name }, { "id", LanNet.Id }, { "secret", LanNet.Secret }, { "nodes", LanNet.Nodes },
                    };
                }
                if (!double.IsNaN(WindowLeft) && !double.IsNaN(WindowTop))
                {
                    d["windowLeft"] = Math.Round(WindowLeft);
                    d["windowTop"] = Math.Round(WindowTop);
                }
                // Written to a side file and swapped in: an interrupted write must never leave a half file,
                // because Load() would then silently fall back to the defaults for every option.
                string json = new JavaScriptSerializer().Serialize(d);
                lock (saveLock)
                {
                    string tmp = Paths.Settings + ".tmp";
                    File.WriteAllText(tmp, json);
                    try
                    {
                        if (File.Exists(Paths.Settings)) File.Replace(tmp, Paths.Settings, null);
                        else File.Move(tmp, Paths.Settings);
                    }
                    catch (IOException)
                    {
                        File.Copy(tmp, Paths.Settings, true);
                        File.Delete(tmp);
                    }
                }
            }
            catch { }
        }

        static readonly object saveLock = new object();
    }

    enum AppMode { All, Only, Except }

    enum VpnEnginePreference { Auto, WireSock }

    enum TunnelEngine { None, WireSock, Wintun }
}
