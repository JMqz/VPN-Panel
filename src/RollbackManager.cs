// VPN Panel - RollbackManager: anti-crash rollback of app updates.

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
    // Automatic crash-loop and 1-click manual rollback manager for updates in Program Files\VPN Panel\rollback\.
    static class RollbackManager
    {
        public static readonly string RollbackDir = Path.Combine(Installer.Dir, "rollback");
        public static readonly string PrevExe = Path.Combine(RollbackDir, "VpnPanel.prev.exe");
        public static readonly string MetaFile = Path.Combine(RollbackDir, "rollback.meta");
        public static readonly string WorkerExe = Path.Combine(RollbackDir, "worker.exe");
        const int AutoRollbackStrikeLimit = 2; // roll back on 3rd launch after 2 failed startup boots

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string existing, string newName, int flags);
        const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        public class RollbackMeta
        {
            public string PrevVersion = "";
            public string TargetVersion = "";
            public bool PendingHealth;
            public int BootStrikes;
            public string CreatedUtc = "";
        }

        public static RollbackMeta ReadMeta(string metaPath, string prevExePath)
        {
            try
            {
                if (!File.Exists(prevExePath) || !File.Exists(metaPath)) return null;
                var fi = new FileInfo(prevExePath);
                if (fi.Length < 100 * 1024) return null;
                var m = new RollbackMeta();
                foreach (string line in File.ReadAllLines(metaPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (k.Equals("prevVersion", StringComparison.OrdinalIgnoreCase)) m.PrevVersion = val;
                    else if (k.Equals("targetVersion", StringComparison.OrdinalIgnoreCase)) m.TargetVersion = val;
                    else if (k.Equals("pendingHealth", StringComparison.OrdinalIgnoreCase)) m.PendingHealth = val == "1";
                    else if (k.Equals("bootStrikes", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out m.BootStrikes);
                    else if (k.Equals("createdUtc", StringComparison.OrdinalIgnoreCase)) m.CreatedUtc = val;
                }
                return string.IsNullOrEmpty(m.PrevVersion) ? null : m;
            }
            catch { return null; }
        }

        public static void WriteMeta(string metaPath, RollbackMeta m)
        {
            if (m == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath));
            string body = string.Join("\r\n", new[]
            {
                "prevVersion=" + (m.PrevVersion ?? ""),
                "targetVersion=" + (m.TargetVersion ?? ""),
                "pendingHealth=" + (m.PendingHealth ? "1" : "0"),
                "bootStrikes=" + m.BootStrikes,
                "createdUtc=" + (m.CreatedUtc ?? DateTime.UtcNow.ToString("o"))
            });
            string tmp = metaPath + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
            try
            {
                File.WriteAllText(tmp, body, new UTF8Encoding(false));
                try { File.Replace(tmp, metaPath, null); }
                catch (IOException) { File.Copy(tmp, metaPath, true); File.Delete(tmp); }
                tmp = null;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            }
        }

        public static bool HasRollbackAvailable()
        {
            return Paths.IsInstalled && ReadMeta(MetaFile, PrevExe) != null;
        }

        public static string AvailablePrevVersion()
        {
            RollbackMeta m = ReadMeta(MetaFile, PrevExe);
            return m != null ? m.PrevVersion : null;
        }

        // Captures a snapshot of the currently installed VpnPanel.exe before Installer.Install overwrites it.
        // Never overwrites an existing snapshot if the current binary is still unverified (pendingHealth == true).
        public static void CapturePreUpdateSnapshot(byte[] newExeBytes)
        {
            CapturePreUpdateSnapshot(Installer.Exe, newExeBytes, Program.Version, RollbackDir, PrevExe, MetaFile);
        }

        public static void CapturePreUpdateSnapshot(
            string currentExePath,
            byte[] newExeBytes,
            string newVersion,
            string rollbackDir,
            string prevExePath,
            string metaPath)
        {
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath)) return;
            try
            {
                byte[] existingBytes = File.ReadAllBytes(currentExePath);
                if (existingBytes.Length < 100 * 1024 || existingBytes[0] != (byte)'M' || existingBytes[1] != (byte)'Z') return;
                if (newExeBytes != null && existingBytes.Length == newExeBytes.Length && existingBytes.SequenceEqual(newExeBytes))
                    return;

                RollbackMeta curMeta = ReadMeta(metaPath, prevExePath);
                if (curMeta != null && curMeta.PendingHealth)
                    return; // previous update never stabilized; preserve the known-good snapshot

                string prevVer = "";
                try { prevVer = FileVersionInfo.GetVersionInfo(currentExePath).ProductVersion ?? ""; } catch { }
                if (prevVer.EndsWith(".0") && prevVer.Split('.').Length == 4)
                    prevVer = prevVer.Substring(0, prevVer.Length - 2);
                if (string.IsNullOrEmpty(prevVer)) prevVer = "anterior";

                Directory.CreateDirectory(rollbackDir);
                string tmp = prevExePath + ".tmp";
                try
                {
                    File.WriteAllBytes(tmp, existingBytes);
                    try { File.Replace(tmp, prevExePath, null); }
                    catch (IOException) { File.Copy(tmp, prevExePath, true); File.Delete(tmp); }
                    tmp = null;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
                }
                try { File.SetLastWriteTimeUtc(prevExePath, File.GetLastWriteTimeUtc(currentExePath)); } catch { }

                WriteMeta(metaPath, new RollbackMeta
                {
                    PrevVersion = prevVer,
                    TargetVersion = newVersion ?? "",
                    PendingHealth = true,
                    BootStrikes = 0,
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                });
            }
            catch { }
        }

        // Called on startup before MainWindow runs. Cleans leftover worker.exe; if the newly installed version
        // already crashed/aborted twice before MarkStartupHealthy(), initiates automatic rollback cleanly.
        public static bool CheckPreLaunchAutoRollback()
        {
            if (!Paths.IsInstalled) return false;
            try
            {
                if (!Paths.Exe.Equals(WorkerExe, StringComparison.OrdinalIgnoreCase) && File.Exists(WorkerExe))
                    File.Delete(WorkerExe);
            }
            catch { }

            RollbackMeta m = ReadMeta(MetaFile, PrevExe);
            if (m == null || !m.PendingHealth) return false;
            if (!string.IsNullOrEmpty(m.TargetVersion) && !m.TargetVersion.Equals(Program.Version, StringComparison.OrdinalIgnoreCase))
                return false;

            if (m.BootStrikes >= AutoRollbackStrikeLimit)
            {
                try
                {
                    Settings s = Settings.Load();
                    if (!string.IsNullOrEmpty(Program.Version))
                    {
                        s.SkippedUpdateVersion = Program.Version;
                        s.Save();
                    }
                }
                catch { }
                if (BeginRollback(true)) return true;
                // Fallback if worker spawn failed: disarm pendingHealth so user isn't stuck in a loop
                m.PendingHealth = false;
                try { WriteMeta(MetaFile, m); } catch { }
                return false;
            }

            m.BootStrikes++;
            try { WriteMeta(MetaFile, m); } catch { }
            return false;
        }

        public static void MarkStartupHealthy()
        {
            if (!Paths.IsInstalled) return;
            try
            {
                if (File.Exists(WorkerExe)) { try { File.Delete(WorkerExe); } catch { } }
                RollbackMeta m = ReadMeta(MetaFile, PrevExe);
                if (m != null && (m.PendingHealth || m.BootStrikes > 0))
                {
                    m.PendingHealth = false;
                    m.BootStrikes = 0;
                    WriteMeta(MetaFile, m);
                }
            }
            catch { }
        }

        // Spawns an elevated worker copy from Program Files\VPN Panel\rollback\worker.exe so the locked main Exe
        // can be atomically replaced with VpnPanel.prev.exe without touching the active VPN tunnel.
        public static bool BeginRollback(bool isAuto)
        {
            if (!HasRollbackAvailable()) return false;
            try
            {
                Directory.CreateDirectory(RollbackDir);
                File.Copy(Paths.Exe, WorkerExe, true);
                int pid = Process.GetCurrentProcess().Id;
                string args = "--apply-rollback --wait-pid " + pid + (isAuto ? " --auto" : "");
                Process.Start(new ProcessStartInfo(WorkerExe, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Installer.Dir
                }).Dispose();
                return true;
            }
            catch { return false; }
        }

        static void AtomicWriteBinaryWithRetry(string targetPath, byte[] bytes, DateTime timestampUtc)
        {
            string tmp = targetPath + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                for (int i = 0; ; i++)
                {
                    try
                    {
                        try { File.Replace(tmp, targetPath, null); }
                        catch (FileNotFoundException) { File.Move(tmp, targetPath); }
                        tmp = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            if (i >= 15) throw;
                            Thread.Sleep(300);
                            continue;
                        }
                        throw;
                    }
                }
                if (timestampUtc != default(DateTime))
                {
                    try { File.SetLastWriteTimeUtc(targetPath, timestampUtc); } catch { }
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            }
        }

        public static void ApplyRollbackFromWorker(string[] args)
        {
            int waitPid = 0;
            bool isAuto = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out waitPid);
                else if (args[i].Equals("--auto", StringComparison.OrdinalIgnoreCase))
                    isAuto = true;
            }

            if (waitPid > 0)
            {
                try
                {
                    using (Process p = Process.GetProcessById(waitPid)) p.WaitForExit(6000);
                }
                catch { }
            }
            Installer.StopApp();

            bool succeeded = false;
            RollbackMeta m = ReadMeta(MetaFile, PrevExe);
            try
            {
                if (m != null)
                {
                    byte[] prevBytes = File.ReadAllBytes(PrevExe);
                    if (prevBytes.Length >= 100 * 1024 && prevBytes[0] == (byte)'M' && prevBytes[1] == (byte)'Z')
                    {
                        DateTime prevTime = File.GetLastWriteTimeUtc(PrevExe);
                        AtomicWriteBinaryWithRetry(Installer.Exe, prevBytes, prevTime);
                        try { AtomicWriteBinaryWithRetry(Installer.ProbeExe, prevBytes, prevTime); } catch { }

                        try
                        {
                            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VPN Panel"))
                            {
                                if (k != null && !string.IsNullOrEmpty(m.PrevVersion))
                                    k.SetValue("DisplayVersion", m.PrevVersion);
                            }
                        }
                        catch { }

                        try { if (File.Exists(PrevExe)) File.Delete(PrevExe); } catch { }
                        try { if (File.Exists(MetaFile)) File.Delete(MetaFile); } catch { }
                        try
                        {
                            NetMon.Record("Atualização", NetMon.EventKind.Warn,
                                (isAuto ? "Rollback automático aplicado para v" : "Rollback manual aplicado para v") + m.PrevVersion);
                        }
                        catch { }
                        succeeded = true;
                    }
                }
            }
            catch { }
            finally
            {
                if (!succeeded && m != null)
                {
                    // Disarm pendingHealth/strikes so a failed rollback never locks the user out of the app
                    m.PendingHealth = false;
                    m.BootStrikes = 0;
                    try { WriteMeta(MetaFile, m); } catch { }
                    try { NetMon.Record("Atualização", NetMon.EventKind.Error, "Falha ao aplicar rollback; reabrindo painel atual"); } catch { }
                }
                try { MoveFileEx(Paths.Exe, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                try
                {
                    if (File.Exists(Installer.Exe))
                        Process.Start(new ProcessStartInfo(Installer.Exe) { WorkingDirectory = Installer.Dir }).Dispose();
                }
                catch { }
            }
        }
    }
}
