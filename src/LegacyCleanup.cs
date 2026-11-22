// VPN Panel - LegacyCleanup: removes leftovers of old versions.

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
    // Safely purges obsolete pre-v3.2 scripts, duplicate portable binaries, stale temp/tasks/Run keys,
    // accidentally persisted split-tunnel DNS comments (with ZIP backup), and orphan network residue without harming user data.
    static class LegacyCleanup
    {
        public class LegacyCleanReport
        {
            public int CleanedCount;
            public string BackupZipPath;
            public readonly List<string> Items = new List<string>();
        }

        static readonly string[] LegacyCvnFiles = new[]
        {
            "vpn-panel.ps1", "ligar-vpn.bat", "vpn-panel.json", "VpnPanel.exe", "VpnPanelProbe.exe"
        };

        public static LegacyCleanReport Run(bool sanitizeUserConfs, bool includeNetworkCleanup)
        {
            return Run(sanitizeUserConfs, includeNetworkCleanup, false);
        }

        public static LegacyCleanReport Run(bool sanitizeUserConfs, bool includeNetworkCleanup, bool assumeInstalled)
        {
            var r = new LegacyCleanReport();
            bool isInstalledContext = assumeInstalled || Paths.IsInstalled;

            // 1. Legacy runtime/LocalAppData probe copies & stale temp/update staging files
            try
            {
                string rtProbe = Path.Combine(Paths.Runtime, ConfFile.ProbeProcessName + ".exe");
                if (File.Exists(rtProbe))
                {
                    File.Delete(rtProbe);
                    r.Items.Add("Removida sonda legada em runtime\\");
                }
            }
            catch { }
            try
            {
                if (isInstalledContext)
                {
                    string localProbe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VPN Panel", ConfFile.ProbeProcessName + ".exe");
                    if (File.Exists(localProbe))
                    {
                        File.Delete(localProbe);
                        r.Items.Add("Removida sonda legada em LocalAppData");
                    }
                }
            }
            catch { }
            foreach (string sub in new[] { "temp", "update" })
            {
                try
                {
                    string dir = Path.Combine(Paths.Data, sub);
                    if (!Directory.Exists(dir)) continue;
                    DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(f) < cutoff)
                            {
                                File.Delete(f);
                                r.Items.Add("Temporário antigo removido: " + Path.GetFileName(f));
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // 2. Legacy C:\VPN & LocalAppData\Programs\VPN Panel remnants only after migration to Program Files
            if (isInstalledContext && File.Exists(Paths.Settings))
            {
                try
                {
                    string legacyCvn = @"C:\VPN";
                    if (Directory.Exists(legacyCvn))
                    {
                        int removedHere = 0;
                        foreach (string fname in LegacyCvnFiles)
                        {
                            string fp = Path.Combine(legacyCvn, fname);
                            if (File.Exists(fp))
                            {
                                try { File.Delete(fp); removedHere++; } catch { }
                            }
                        }
                        if (Directory.GetFileSystemEntries(legacyCvn).Length == 0)
                            try { Directory.Delete(legacyCvn); removedHere++; } catch { }
                        if (removedHere > 0) r.Items.Add("Removidos vestígios antigos em C:\\VPN (" + removedHere + ")");
                    }
                }
                catch { }

                try
                {
                    string oldLocalProg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "VPN Panel");
                    if (Directory.Exists(oldLocalProg) && !oldLocalProg.Equals(Paths.ExeDir, StringComparison.OrdinalIgnoreCase))
                    {
                        int removedHere = 0;
                        foreach (string fname in new[] { "VpnPanel.exe", "VpnPanelProbe.exe" })
                        {
                            string fp = Path.Combine(oldLocalProg, fname);
                            if (File.Exists(fp)) { try { File.Delete(fp); removedHere++; } catch { } }
                        }
                        if (Directory.GetFileSystemEntries(oldLocalProg).Length == 0)
                            try { Directory.Delete(oldLocalProg); removedHere++; } catch { }
                        if (removedHere > 0) r.Items.Add("Removida instalação antiga em LocalAppData\\Programs");
                    }
                }
                catch { }
            }

            // 3. Legacy Windows Run registry keys (modern versions use Task Scheduler)
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine })
            {
                try
                {
                    using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Default))
                    using (var runKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (runKey == null) continue;
                        foreach (string valName in runKey.GetValueNames())
                        {
                            string val = Convert.ToString(runKey.GetValue(valName)) ?? "";
                            bool matchName = valName.Equals("VPN Panel", StringComparison.OrdinalIgnoreCase) ||
                                             valName.Equals("VPNPanel", StringComparison.OrdinalIgnoreCase) ||
                                             valName.Equals("vpn-panel", StringComparison.OrdinalIgnoreCase);
                            bool matchOurTarget = val.IndexOf("vpn-panel.ps1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  val.IndexOf("VpnPanel.exe", StringComparison.OrdinalIgnoreCase) >= 0;
                            if ((matchName && (matchOurTarget || val.IndexOf(@"C:\VPN\", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                val.IndexOf("vpn-panel.ps1", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    runKey.DeleteValue(valName, false);
                                    r.Items.Add("Entrada de inicialização legada removida (" + hive + "): " + valName);
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            // 4. Obsolete scheduled tasks (older scripts only; never delete Tasks.Logon / Tasks.Open so startup setting is preserved)
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                var toDelete = new List<string>();
                foreach (dynamic t in folder.GetTasks(1))
                {
                    try
                    {
                        string tname = (string)t.Name;
                        if (string.IsNullOrEmpty(tname)) continue;
                        if (tname.Equals(Tasks.Logon, StringComparison.OrdinalIgnoreCase) ||
                            tname.Equals(Tasks.Open, StringComparison.OrdinalIgnoreCase))
                            continue;
                        bool isVpnTask = tname.StartsWith("VPN Panel", StringComparison.OrdinalIgnoreCase) ||
                                         tname.StartsWith("VPNPanel", StringComparison.OrdinalIgnoreCase) ||
                                         tname.StartsWith("vpn-panel", StringComparison.OrdinalIgnoreCase);
                        if (isVpnTask) toDelete.Add(tname);
                    }
                    catch { }
                }
                foreach (string name in toDelete)
                {
                    try
                    {
                        folder.DeleteTask(name, 0);
                        r.Items.Add("Tarefa agendada obsoleta removida: " + name);
                    }
                    catch { }
                }
            }
            catch { }

            // 5. Sanitize accidentally persisted split-tunnel DNS comments in user configs (with automatic ZIP backup)
            if (sanitizeUserConfs)
            {
                string backupZip;
                int sanitized = ConfFile.SanitizeLegacyConfs(out backupZip);
                r.BackupZipPath = backupZip;
                if (sanitized > 0)
                    r.Items.Add("Comentários antigos de DNS restaurados em " + sanitized + " servidor(es)");
            }

            // 6. Our own network leftovers when no tunnel is currently active (never stops external WireSock service unasked)
            if (includeNetworkCleanup && !Tunnel.Active(true) && !Tunnel.IsWintunServiceRunning())
            {
                int orphans;
                bool lockReset, ghostCleaned;
                if (Tunnel.TryIdleCleanup(true, true, false, true, true, () => false,
                    out orphans, out lockReset, out ghostCleaned))
                {
                    if (orphans > 0) r.Items.Add("Processos órfãos encerrados (" + orphans + ")");
                    if (lockReset) r.Items.Add("Kill Switch residual (WFP) liberado");
                    if (ghostCleaned) r.Items.Add("Rotas IPv6 fantasmas e cache DNS limpos");
                }
                List<string> restoredDns;
                if (AutoHealer.RestoreStuckPhysicalDns(out restoredDns))
                    r.Items.Add("DNS preso em placa física restaurado: " + string.Join(", ", restoredDns.ToArray()));
            }

            r.CleanedCount = r.Items.Count;
            if (r.CleanedCount > 0)
                NetMon.Record("Limpeza", NetMon.EventKind.Info, "Limpeza de legado concluída (" + r.CleanedCount + " item(ns))");
            return r;
        }
    }
}
