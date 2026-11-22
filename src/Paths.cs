// VPN Panel - Paths: install/data folders, WireSock/WireGuard detection and the server list cache.

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
    static class Paths
    {
        public static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Exe = Assembly.GetExecutingAssembly().Location;
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPN Panel");
        public static readonly string Configs = Path.Combine(Data, "configs");
        public static readonly string Runtime = Path.Combine(Data, "runtime");
        public static readonly string WintunRuntimeDir = Path.Combine(Runtime, "wintun");
        public static readonly string WintunRuntimeConf = Path.Combine(WintunRuntimeDir, "VpnPanel.conf");
        public static readonly string WintunActiveFlag = Path.Combine(Data, "wintun.active");
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

        static readonly string[] WireGuardCandidates = new[]
        {
            @"%ProgramFiles%\WireGuard\wireguard.exe",
            @"%ProgramFiles(x86)%\WireGuard\wireguard.exe",
        }.Select(Environment.ExpandEnvironmentVariables).ToArray();

        static string wireGuardFound;
        static DateTime wireGuardSearched;
        static DateTime wireGuardVerified;

        public static void ForgetWireGuard()
        {
            wireGuardFound = null;
            wireGuardSearched = DateTime.MinValue;
            wireGuardVerified = DateTime.MinValue;
        }

        public static string WireGuard
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                if (wireGuardFound != null)
                {
                    if ((now - wireGuardVerified).TotalSeconds < 30) return wireGuardFound;
                    if (File.Exists(wireGuardFound)) { wireGuardVerified = now; return wireGuardFound; }
                    wireGuardFound = null;
                }
                if ((now - wireGuardSearched).TotalSeconds < 30) return null;
                wireGuardSearched = now;
                wireGuardFound = WireGuardCandidates.FirstOrDefault(File.Exists);
                if (wireGuardFound != null) wireGuardVerified = now;
                return wireGuardFound;
            }
        }

        public static string WgCli
        {
            get
            {
                string wg = WireGuard;
                if (wg == null) return null;
                try
                {
                    string cli = Path.Combine(Path.GetDirectoryName(wg), "wg.exe");
                    return File.Exists(cli) ? cli : null;
                }
                catch { return null; }
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
}
