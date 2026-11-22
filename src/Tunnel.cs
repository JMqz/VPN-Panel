// VPN Panel - Tunnel: starts, tracks and stops the WireSock process / Wintun service.

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
    // Manages the active VPN backend (WireGuard/Wintun in All mode when installed, or WireSock NDIS for split tunneling)
    static class Tunnel
    {
        public const string WintunTunnelName = "VpnPanel";
        public const string WintunServiceName = "WireGuardTunnel$VpnPanel";

        static readonly object stateLock = new object();
        static readonly object stopLock = new object();
        static int stoppingCount;
        static int currentPid;
        static IntPtr currentHandle;
        static bool lockHint; // the tracked WireSock tunnel runs with -network-lock
        static TunnelEngine activeEngine;
        static TunnelEngine lastActiveEngine;
        static string lastStartedConfig;
        static DateTime lastStartedAt;
        static bool ipv6Isolated;
        static bool probeAppInjected;
        static int lastExitCode;
        static DateTime startTime;
        static long startUtcUnix;
        static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static TunnelEngine ActiveEngine
        {
            get { lock (stateLock) return activeEngine; }
            private set { lock (stateLock) activeEngine = value; }
        }
        public static TunnelEngine LastActiveEngine
        {
            get { lock (stateLock) return lastActiveEngine; }
            private set { lock (stateLock) lastActiveEngine = value; }
        }
        public static string LastStartedConfig
        {
            get { lock (stateLock) return lastStartedConfig; }
            private set { lock (stateLock) lastStartedConfig = value; }
        }
        public static DateTime LastStartedAt
        {
            get { lock (stateLock) return lastStartedAt; }
            private set { lock (stateLock) lastStartedAt = value; }
        }
        public static bool Ipv6Isolated
        {
            get { lock (stateLock) return ipv6Isolated; }
            private set { lock (stateLock) ipv6Isolated = value; }
        }
        public static bool ProbeAppInjected
        {
            get { lock (stateLock) return probeAppInjected; }
            private set { lock (stateLock) probeAppInjected = value; }
        }
        public static int LastExitCode
        {
            get { lock (stateLock) return lastExitCode; }
            private set { lock (stateLock) lastExitCode = value; }
        }
        public static int Pid { get { lock (stateLock) return currentPid; } }
        public static DateTime StartTime
        {
            get { lock (stateLock) return startTime; }
            private set { lock (stateLock) startTime = value; }
        }
        static volatile bool connecting;
        public static bool Connecting { get { return connecting; } set { connecting = value; } }
        public static bool IsStopping { get { return Interlocked.CompareExchange(ref stoppingCount, 0, 0) > 0; } }
        public static bool HasPendingNetworkLock
        {
            get { lock (stateLock) return lockHint || File.Exists(Paths.NetworkLockFlag); }
        }

        static long ToUnixSeconds(DateTime utc)
        {
            return (long)(utc - UnixEpoch).TotalSeconds;
        }

        public static bool IsWintunServiceRunning()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(WintunServiceName))
                {
                    var st = sc.Status;
                    return st == System.ServiceProcess.ServiceControllerStatus.Running ||
                           st == System.ServiceProcess.ServiceControllerStatus.StartPending;
                }
            }
            catch { return false; }
        }

        static bool WintunServiceExists()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(WintunServiceName))
                {
                    var st = sc.Status;
                    return st != System.ServiceProcess.ServiceControllerStatus.Stopped || true;
                }
            }
            catch { return false; }
        }

        public static string ReadWintunActiveConfig()
        {
            try
            {
                if (!File.Exists(Paths.WintunActiveFlag)) return null;
                string s = File.ReadAllText(Paths.WintunActiveFlag).Trim();
                return s.Length > 0 ? s : null;
            }
            catch { return null; }
        }

        public static DateTime FetchWintunServiceStartTime()
        {
            DateTime found = default(DateTime);
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Service WHERE Name='" + WintunServiceName.Replace("'", "''") + "'"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        int pid = Convert.ToInt32(o["ProcessId"]);
                        if (pid > 0)
                        {
                            IntPtr h = ProcUtil.OpenForMonitor(pid);
                            if (h != IntPtr.Zero)
                            {
                                try { found = ProcUtil.StartTime(h); }
                                finally { ProcUtil.Close(ref h); }
                                break;
                            }
                        }
                    }
                }
            }
            catch { }
            lock (stateLock)
            {
                if (found != default(DateTime))
                {
                    startTime = found;
                    startUtcUnix = ToUnixSeconds(found.ToUniversalTime());
                }
                else if (startTime == default(DateTime))
                {
                    startTime = DateTime.Now;
                    startUtcUnix = ToUnixSeconds(DateTime.UtcNow);
                }
                return startTime;
            }
        }

        public static TunnelEngine ResolveEngine(AppMode mode, VpnEnginePreference pref)
        {
            return ResolveEngine(null, mode, pref);
        }

        public static TunnelEngine ResolveEngine(string config, AppMode mode, VpnEnginePreference pref)
        {
            if (pref == VpnEnginePreference.Auto && mode == AppMode.All && Paths.WireGuard != null)
            {
                if (string.IsNullOrEmpty(config) || ConfFile.IsWintunCompatible(config))
                    return TunnelEngine.Wintun;
            }
            return TunnelEngine.WireSock;
        }

        static bool StopWintunService(int waitMs)
        {
            int boundedWait = Math.Max(300, waitMs);
            bool uninstalled = true;
            string wgExe = Paths.WireGuard;
            if (WintunServiceExists())
            {
                uninstalled = false;
                if (wgExe != null)
                {
                    try
                    {
                        string outStr, errStr;
                        int code = ProcUtil.RunBounded(wgExe, "/uninstalltunnelservice " + WintunTunnelName, boundedWait, out outStr, out errStr);
                        if (code == 0 && !IsWintunServiceRunning()) uninstalled = true;
                    }
                    catch { }
                }
                if (!uninstalled)
                {
                    try
                    {
                        using (var sc = new System.ServiceProcess.ServiceController(WintunServiceName))
                        {
                            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped &&
                                sc.Status != System.ServiceProcess.ServiceControllerStatus.StopPending)
                            {
                                sc.Stop();
                            }
                            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(boundedWait));
                        }
                    }
                    catch { }
                    try
                    {
                        string scOut, scErr;
                        ProcUtil.RunBounded("sc.exe", "delete \"" + WintunServiceName + "\"", Math.Min(1500, boundedWait), out scOut, out scErr);
                    }
                    catch { }
                    uninstalled = !IsWintunServiceRunning();
                }
            }
            if (!IsWintunServiceRunning())
            {
                try { if (File.Exists(Paths.WintunActiveFlag)) File.Delete(Paths.WintunActiveFlag); } catch { }
                try { if (File.Exists(Paths.WintunRuntimeConf)) File.Delete(Paths.WintunRuntimeConf); } catch { }
            }
            return uninstalled;
        }

        public static void PollWintunStatus(bool currentlyHandshakeOk, out bool sawHs, out bool hsOk, out bool hsFailed)
        {
            sawHs = false;
            hsOk = false;
            hsFailed = false;
            string wgCli = Paths.WgCli;
            if (wgCli == null) return;
            try
            {
                string stdout, stderr;
                int code = ProcUtil.RunBounded(wgCli, "show " + WintunTunnelName + " latest-handshakes", 1500, out stdout, out stderr);
                if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return;
                long maxHsUnix = 0;
                foreach (string rawLine in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = rawLine.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    long ts;
                    if (long.TryParse(parts[parts.Length - 1], out ts) && ts > maxHsUnix)
                        maxHsUnix = ts;
                }
                sawHs = true;
                if (maxHsUnix <= 0) return;

                long sessStart;
                lock (stateLock) sessStart = startUtcUnix;
                long nowUnix = ToUnixSeconds(DateTime.UtcNow);
                if (sessStart > 0 && maxHsUnix < sessStart - 5) return; // stale handshake from before this session
                long age = nowUnix - maxHsUnix;
                if (age <= 165)
                {
                    hsOk = true;
                }
                else if (currentlyHandshakeOk)
                {
                    hsFailed = true;
                }
            }
            catch { }
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
                startTime = ProcUtil.StartTime(handle);
                startUtcUnix = startTime != default(DateTime) ? ToUnixSeconds(startTime.ToUniversalTime()) : ToUnixSeconds(DateTime.UtcNow);
            }
        }

        // True while the tracked client lives, avoiding a full system process scan on every tick.
        // Holding currentHandle prevents Windows from reusing the PID while open.
        public static bool Active(bool allowScan)
        {
            if (Interlocked.CompareExchange(ref stoppingCount, 0, 0) > 0) return false;
            bool wasWintun;
            lock (stateLock)
            {
                if (currentPid != 0)
                {
                    if (ProcUtil.IsAlive(currentHandle))
                    {
                        activeEngine = TunnelEngine.WireSock;
                        lastActiveEngine = TunnelEngine.WireSock;
                        return true;
                    }
                    lastExitCode = ProcUtil.ExitCode(currentHandle);
                    ProcUtil.Close(ref currentHandle);
                    currentPid = 0;
                }
                wasWintun = activeEngine == TunnelEngine.Wintun;
            }
            if (wasWintun || allowScan)
            {
                if (IsWintunServiceRunning())
                {
                    lock (stateLock)
                    {
                        activeEngine = TunnelEngine.Wintun;
                        lastActiveEngine = TunnelEngine.Wintun;
                        if (startTime == default(DateTime))
                        {
                            startTime = DateTime.Now;
                            startUtcUnix = ToUnixSeconds(DateTime.UtcNow);
                        }
                    }
                    return true;
                }
                lock (stateLock)
                {
                    if (activeEngine == TunnelEngine.Wintun) activeEngine = TunnelEngine.None;
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
                if (found && currentPid == 0 && startTime == default(DateTime))
                {
                    startTime = DateTime.Now;
                    startUtcUnix = ToUnixSeconds(DateTime.UtcNow);
                }
                activeEngine = found ? TunnelEngine.WireSock : TunnelEngine.None;
                if (found) lastActiveEngine = TunnelEngine.WireSock;
            }
            return found;
        }

        // Called with the command line of a tunnel this instance did not start
        public static void NoteCommandLine(string commandLine)
        {
            NoteCommandLine(commandLine, (bool?)null);
        }

        public static void NoteCommandLine(string commandLine, bool? probeAppInjectedOverride)
        {
            bool hasLock = commandLine != null && Regex.IsMatch(commandLine, "-network-lock\\s+(enabled|on)\\b", RegexOptions.IgnoreCase);
            lock (stateLock) lockHint = hasLock;
            string cfg = ConfigOf(commandLine);
            ConfigInfo info = cfg != null ? ConfigInfo.Read(cfg) : null;
            Ipv6Isolated = commandLine != null &&
                           commandLine.IndexOf(Paths.Runtime, StringComparison.OrdinalIgnoreCase) >= 0 &&
                           (info == null || info.Mode != AppMode.All);
            ProbeAppInjected = Ipv6Isolated && info != null && info.Mode == AppMode.Only &&
                               (probeAppInjectedOverride.HasValue ? probeAppInjectedOverride.Value : NetDiag.RuntimeHasProbeApp(cfg));
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
            Start(exe, config, networkLock, 0, 0, null, VpnEnginePreference.Auto);
        }

        public static void Start(string exe, string config, bool networkLock, int maxMtu)
        {
            Start(exe, config, networkLock, maxMtu, 0, null, VpnEnginePreference.Auto);
        }

        public static void Start(string exe, string config, bool networkLock, int maxMtu, int keepaliveSec, string customDns)
        {
            Start(exe, config, networkLock, maxMtu, keepaliveSec, customDns, VpnEnginePreference.Auto);
        }

        public static void Start(string exe, string config, bool networkLock, int maxMtu, int keepaliveSec, string customDns, VpnEnginePreference enginePref)
        {
            lock (stopLock)
            {
                bool hadPendingWsLock = HasPendingNetworkLock;
                // Never leave an orphaned WireSock or Wintun tunnel fighting each other
                bool allWsStopped = true;
                foreach (Process existing in Find())
                {
                    allWsStopped = ProcUtil.Shutdown(existing.Id, 2000) && allWsStopped;
                    existing.Dispose();
                }
                bool wintunStopped = StopWintunService(2500);

                ConfigInfo info = ConfigInfo.Read(config);
                AppMode mode = info != null ? info.Mode : AppMode.All;
                // Kill switch blocks svchost.exe (local DNS) and non-tunneled apps in split-tunneling modes
                if (mode != AppMode.All) networkLock = false;

                TunnelEngine wantedEngine = ResolveEngine(config, mode, enginePref);
                if (wantedEngine == TunnelEngine.WireSock && !wintunStopped)
                    throw new InvalidOperationException("O serviço WireGuard/Wintun anterior não encerrou a tempo.");
                LastStartedConfig = config;
                LastStartedAt = DateTime.UtcNow;
                LastExitCode = 0;

                if (wantedEngine == TunnelEngine.Wintun)
                {
                    string wgExe = Paths.WireGuard;
                    if (string.IsNullOrEmpty(wgExe))
                        throw new InvalidOperationException("WireGuard (Wintun) não encontrado.");

                    if (hadPendingWsLock || !allWsStopped)
                        ResetNetworkLock(2500);

                    Ipv6Isolated = false;
                    ProbeAppInjected = false;
                    lock (stateLock) lockHint = false;
                    ClearNetworkLockFlag();
                    if (ConfFile.HasGhostIpv6()) ConfFile.CleanGhostNetwork();

                    string wgConf = ConfFile.WintunPath(config, maxMtu, keepaliveSec, customDns, networkLock);
                    try
                    {
                        Directory.CreateDirectory(Paths.Data);
                        File.WriteAllText(Paths.WintunActiveFlag, config ?? "");
                    }
                    catch { }

                    string outText, errText;
                    int code = ProcUtil.RunBounded(wgExe, "/installtunnelservice \"" + wgConf + "\"", 12000, out outText, out errText);
                    if (code != 0 || !IsWintunServiceRunning())
                    {
                        StopWintunService(2000);
                        string detail = !string.IsNullOrWhiteSpace(errText) ? errText.Trim() : outText.Trim();
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                            ? "Falha ao iniciar o serviço WireGuard/Wintun (código " + code + ")."
                            : detail);
                    }
                    lock (stateLock)
                    {
                        activeEngine = TunnelEngine.Wintun;
                        lastActiveEngine = TunnelEngine.Wintun;
                        startTime = DateTime.Now;
                        startUtcUnix = ToUnixSeconds(DateTime.UtcNow);
                    }
                    return;
                }

                bool v6Stripped, probeInjected;
                string confPath = ConfFile.EffectivePath(config, maxMtu, keepaliveSec, customDns, out v6Stripped, out probeInjected);
                Ipv6Isolated = v6Stripped;
                ProbeAppInjected = probeInjected;
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
                    lock (stateLock)
                    {
                        activeEngine = TunnelEngine.WireSock;
                        lastActiveEngine = TunnelEngine.WireSock;
                        lockHint = networkLock;
                    }
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
            Stop(resetLock, 5000);
        }

        // Bounded fast teardown used during Windows shutdown/logoff so WM_QUERYENDSESSION never stalls.
        public static void StopForShutdown(bool resetLock)
        {
            Stop(resetLock, 1200);
        }

        static void Stop(bool resetLock, int waitMs)
        {
            Interlocked.Increment(ref stoppingCount);
            try
            {
                lock (stopLock)
                {
                    bool hadWintun = IsWintunServiceRunning() || File.Exists(Paths.WintunActiveFlag);
                    bool wintunStopped = StopWintunService(waitMs);
                    bool hadLockFlag;
                    int pidToStop;
                    lock (stateLock)
                    {
                        hadLockFlag = lockHint || File.Exists(Paths.NetworkLockFlag);
                        lockHint = false;
                        pidToStop = currentPid;
                    }
                    resetLock |= hadLockFlag;
                    ReleaseCurrent();
                    bool anyFound = pidToStop != 0;
                    bool wsStopped = true;
                    // Ctrl+C first: WireSock then drops the tunnel and releases the lock by itself
                    if (pidToStop != 0) wsStopped = ProcUtil.Shutdown(pidToStop, waitMs);
                    foreach (Process p in Find())
                    {
                        anyFound = true;
                        wsStopped = ProcUtil.Shutdown(p.Id, waitMs) && wsStopped;
                        p.Dispose();
                    }
                    bool allStopped = wsStopped && (!hadWintun || wintunStopped);
                    if (allStopped)
                    {
                        LastStartedConfig = null;
                        Ipv6Isolated = false;
                        ProbeAppInjected = false;
                        ActiveEngine = TunnelEngine.None;
                        StartTime = default(DateTime);
                        lock (stateLock) startUtcUnix = 0;
                    }
                    else if (hadWintun && !wintunStopped)
                    {
                        NetMon.Record("WireGuard", NetMon.EventKind.Error, "Serviço Wintun não encerrou dentro do tempo limite");
                    }
                    if (!anyFound) wsStopped = hadWintun ? wintunStopped : false;
                    ConfFile.DeleteRuntimeCopies();
                    if (ConfFile.HasGhostIpv6()) ConfFile.CleanGhostNetwork(false);
                    if (resetLock && !wsStopped && (anyFound || hadLockFlag || !hadWintun)) ResetNetworkLock(waitMs);
                    else if (wsStopped) ClearNetworkLockFlag();
                }
            }
            finally
            {
                Interlocked.Decrement(ref stoppingCount);
            }
        }

        public static void ResetNetworkLock()
        {
            ResetNetworkLock(5000);
        }

        static void ResetNetworkLock(int waitMs)
        {
            lock (stopLock)
            {
                ConfFile.CleanGhostNetwork(false);
                string exe = Paths.WireSock;
                if (exe != null)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(exe, "reset-network-lock") { UseShellExecute = false, CreateNoWindow = true };
                        using (Process p = Process.Start(psi)) p.WaitForExit(waitMs);
                    }
                    catch { }
                }
                ConfFile.FlushDnsCache();
                ClearNetworkLockFlag();
            }
        }

        // Serializes idle cleanup with Tunnel.Start/Stop under stopLock so background or manual
        // repair can never reset WFP or delete runtime files for an active/starting tunnel.
        public static bool TryIdleCleanup(
            bool killOrphans,
            bool resetLockIfPending,
            bool forceDnsFlush,
            bool cleanGhostV6,
            Func<bool> isBusy,
            out int orphansKilled,
            out bool lockReset,
            out bool ghostOrDnsCleaned)
        {
            return TryIdleCleanup(killOrphans, resetLockIfPending, false, forceDnsFlush, cleanGhostV6, isBusy,
                out orphansKilled, out lockReset, out ghostOrDnsCleaned);
        }

        public static bool TryIdleCleanup(
            bool killOrphans,
            bool resetLockIfPending,
            bool forceResetLock,
            bool forceDnsFlush,
            bool cleanGhostV6,
            Func<bool> isBusy,
            out int orphansKilled,
            out bool lockReset,
            out bool ghostOrDnsCleaned)
        {
            orphansKilled = 0;
            lockReset = false;
            ghostOrDnsCleaned = false;
            lock (stopLock)
            {
                bool activeNow = (killOrphans ? Active(false) : Active(true)) || IsWintunServiceRunning();
                if (Connecting || IsStopping || activeNow || (isBusy != null && isBusy()))
                    return false;

                if (killOrphans)
                {
                    try
                    {
                        Process[] procs = Find();
                        for (int i = 0; i < procs.Length; i++)
                        {
                            Process p = procs[i];
                            try
                            {
                                if (ProcUtil.Shutdown(p.Id, 2000))
                                {
                                    orphansKilled++;
                                }
                                else
                                {
                                    p.Kill();
                                    p.WaitForExit(1500);
                                    orphansKilled++;
                                }
                            }
                            catch { }
                            finally { try { p.Dispose(); } catch { } }
                        }
                    }
                    catch { }
                }

                bool hasRuntime = false;
                try
                {
                    hasRuntime = Directory.Exists(Paths.Runtime) && Directory.GetFiles(Paths.Runtime, "*.conf").Length > 0;
                }
                catch { }

                bool hasLock = false;
                try { hasLock = HasPendingNetworkLock; } catch { }

                bool hasGhost = false;
                if (cleanGhostV6)
                {
                    try { hasGhost = ConfFile.HasGhostIpv6(); } catch { }
                }

                if (forceResetLock || (resetLockIfPending && (hasLock || hasRuntime)) || orphansKilled > 0)
                {
                    ConfFile.DeleteRuntimeCopies();
                    ResetNetworkLock();
                    lockReset = true;
                    if (forceDnsFlush || hasGhost) ghostOrDnsCleaned = true;
                }
                else if (forceDnsFlush || hasGhost)
                {
                    ConfFile.CleanGhostNetwork(true);
                    ghostOrDnsCleaned = true;
                }

                return true;
            }
        }
    }
}
