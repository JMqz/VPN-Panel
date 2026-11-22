// VPN Panel - DiscordWatch (is Discord open and in a call or stream?) and LinkQuality (rolling
// ping / jitter / loss of the tunnel path). Both feed the Live mode in MainWindow.Live.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VpnPanel
{
    // Discord's voice / Go Live engine runs inside a renderer process and opens one UDP socket per media
    // connection (a call, a stream being sent or watched). Chromium's own UDP (QUIC, mDNS) lives in the
    // separate NetworkService utility process. So "a Discord process that is not the network service owns
    // UDP sockets" means a call or a stream is in progress. It costs one read of the system UDP table:
    // no packet capture, no admin rights, nothing injected into Discord.
    static class DiscordWatch
    {
        public class Snapshot
        {
            public bool Running;
            public int MediaSockets;
            public bool InCall { get { return MediaSockets > 0; } }
        }

        static readonly string[] ProcessNames = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };

        // pid -> is it Chromium's network service (whose UDP sockets are not media)
        static readonly Dictionary<int, bool> networkServicePids = new Dictionary<int, bool>();
        static readonly object sync = new object();
        const int PidCacheSeconds = 12;
        static HashSet<int> cachedPids;
        static long cachedPidsAt;

        // An entry of the split tunneling list that matches Discord ("Discord", "discord.exe", a full path...)
        public static bool IsDiscordApp(string app)
        {
            return !string.IsNullOrEmpty(app) && app.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Does this routing send Discord through the tunnel?
        public static bool TunnelsDiscord(AppMode mode, IList<string> apps)
        {
            if (mode == AppMode.All) return true;
            bool listed = false;
            if (apps != null)
                for (int i = 0; i < apps.Count; i++)
                    if (IsDiscordApp(apps[i])) { listed = true; break; }
            return mode == AppMode.Only ? listed : !listed;
        }

        // Blocking (a few ms): call it off the UI thread.
        public static Snapshot Scan()
        {
            var snap = new Snapshot();
            lock (sync)
            {
                // Listing processes by name is the costly part (a few ms), so the pid set is reused for a
                // while; the UDP table itself is read on every scan.
                if (cachedPids == null || Stopwatch.GetTimestamp() - cachedPidsAt > PidCacheSeconds * Stopwatch.Frequency)
                {
                    var found = new HashSet<int>();
                    for (int i = 0; i < ProcessNames.Length; i++)
                    {
                        Process[] procs;
                        try { procs = Process.GetProcessesByName(ProcessNames[i]); }
                        catch { continue; }
                        for (int j = 0; j < procs.Length; j++)
                        {
                            try { found.Add(procs[j].Id); } catch { }
                            try { procs[j].Dispose(); } catch { }
                        }
                    }
                    cachedPids = found;
                    cachedPidsAt = Stopwatch.GetTimestamp();
                }
                HashSet<int> pids = cachedPids;
                snap.Running = pids.Count > 0;
                if (!snap.Running)
                {
                    networkServicePids.Clear();
                    return snap;
                }
                var dead = new List<int>();
                foreach (int known in networkServicePids.Keys)
                    if (!pids.Contains(known)) dead.Add(known);
                for (int i = 0; i < dead.Count; i++) networkServicePids.Remove(dead[i]);

                var seen = new HashSet<long>();
                foreach (KeyValuePair<int, int> row in UdpOwners())
                {
                    int pid = row.Key, port = row.Value;
                    if (!pids.Contains(pid)) continue;
                    if (port == 0 || port == 5353 || port == 1900) continue; // mDNS / SSDP discovery
                    if (!seen.Add(((long)pid << 16) | (uint)port)) continue; // dual-stack socket listed twice
                    if (IsNetworkService(pid)) continue;
                    snap.MediaSockets++;
                }
            }
            return snap;
        }

        static bool IsNetworkService(int pid)
        {
            bool known;
            if (networkServicePids.TryGetValue(pid, out known)) return known;
            string cmd = null;
            try { cmd = ProcUtil.CommandLine(pid); } catch { }
            // Unreadable command line: leave it out rather than report a call that may not exist
            if (string.IsNullOrEmpty(cmd)) return true;
            // Exact switch: other Discord processes carry "NetworkService..." inside their --enable-features list
            bool isNet = cmd.IndexOf("--utility-sub-type=network.mojom.NetworkService", StringComparison.OrdinalIgnoreCase) >= 0;
            networkServicePids[pid] = isNet;
            return isNet;
        }

        const int AF_INET = 2, AF_INET6 = 23, UDP_TABLE_OWNER_PID = 1;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        // (owning pid, local port) of every UDP socket on the machine, IPv4 and IPv6
        static List<KeyValuePair<int, int>> UdpOwners()
        {
            var list = new List<KeyValuePair<int, int>>(128);
            ReadUdpTable(AF_INET, 12, 4, 8, list);    // MIB_UDPROW_OWNER_PID: addr, port, pid
            ReadUdpTable(AF_INET6, 28, 20, 24, list); // MIB_UDP6ROW_OWNER_PID: addr[16], scope, port, pid
            return list;
        }

        static void ReadUdpTable(int family, int rowSize, int portOffset, int pidOffset, List<KeyValuePair<int, int>> list)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = 0;
                uint rc = GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
                for (int attempt = 0; attempt < 3 && rc == ERROR_INSUFFICIENT_BUFFER && size > 0; attempt++)
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    size += 1024; // room for sockets opened between the two calls
                    buf = Marshal.AllocHGlobal(size);
                    rc = GetExtendedUdpTable(buf, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
                }
                if (rc != 0 || buf == IntPtr.Zero) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    int rowAt = 4 + i * rowSize;
                    if (rowAt + rowSize > size) break;
                    int rawPort = Marshal.ReadInt32(buf, rowAt + portOffset);
                    int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF); // network byte order
                    int pid = Marshal.ReadInt32(buf, rowAt + pidOffset);
                    list.Add(new KeyValuePair<int, int>(pid, port));
                }
            }
            catch { }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }
    }

    // Rolling quality of the path through the tunnel, fed one probe result at a time (UI thread only).
    class LinkQuality
    {
        public enum Rating { Unknown, Great, Good, Unstable, Bad }
        // Who dropped a probe: the VPN path (direct internet answered), the user's own internet, or unknown
        public enum Cause { None, Vpn, Internet, Unknown }

        const int WindowSize = 30;

        struct Sample { public bool Ok; public int Ms; }

        readonly Queue<Sample> window = new Queue<Sample>(WindowSize + 1);

        public int Count { get { return window.Count; } }
        public int AvgMs { get; private set; }
        public int JitterMs { get; private set; }
        public int LossPct { get; private set; }
        public int ConsecutiveLost { get; private set; }

        // Totals since ResetSession (one Discord call)
        public int SessionSamples { get; private set; }
        public int SessionLost { get; private set; }
        long sessionMsSum;
        int sessionOk;
        public int SessionAvgMs { get { return sessionOk > 0 ? (int)Math.Round(sessionMsSum / (double)sessionOk) : 0; } }
        public double SessionLossPct { get { return SessionSamples > 0 ? SessionLost * 100.0 / SessionSamples : 0; } }

        public void Add(bool ok, int ms)
        {
            window.Enqueue(new Sample { Ok = ok, Ms = ok ? Math.Max(1, ms) : 0 });
            while (window.Count > WindowSize) window.Dequeue();
            ConsecutiveLost = ok ? 0 : ConsecutiveLost + 1;
            SessionSamples++;
            if (ok) { sessionOk++; sessionMsSum += Math.Max(1, ms); }
            else SessionLost++;

            int okCount = 0, sum = 0, jitterSum = 0, jitterPairs = 0, prev = -1;
            foreach (Sample s in window)
            {
                if (!s.Ok) { prev = -1; continue; }
                okCount++;
                sum += s.Ms;
                if (prev >= 0) { jitterSum += Math.Abs(s.Ms - prev); jitterPairs++; }
                prev = s.Ms;
            }
            AvgMs = okCount > 0 ? (int)Math.Round(sum / (double)okCount) : 0;
            JitterMs = jitterPairs > 0 ? (int)Math.Round(jitterSum / (double)jitterPairs) : 0;
            LossPct = (int)Math.Round((window.Count - okCount) * 100.0 / window.Count);
        }

        // Old samples say nothing about now (sampling was paused)
        public void ResetWindow()
        {
            window.Clear();
            AvgMs = JitterMs = LossPct = 0;
            ConsecutiveLost = 0;
        }

        public void ResetSession()
        {
            SessionSamples = SessionLost = sessionOk = 0;
            sessionMsSum = 0;
        }

        public Rating Rate()
        {
            if (window.Count < 3) return Rating.Unknown;
            if (LossPct >= 10 || ConsecutiveLost >= 3) return Rating.Bad;
            if (LossPct >= 3 || JitterMs >= 30 || AvgMs >= 250) return Rating.Unstable;
            if (LossPct == 0 && JitterMs < 10 && AvgMs < 120) return Rating.Great;
            return Rating.Good;
        }

        public static string RatingText(Rating r)
        {
            switch (r)
            {
                case Rating.Great: return "Ótima";
                case Rating.Good: return "Boa";
                case Rating.Unstable: return "Instável";
                case Rating.Bad: return "Ruim";
                default: return "";
            }
        }

        // "38 ms · osc. 3 ms · perda 0%" (ping, jitter, loss): short enough for the card on Início
        public string Summary()
        {
            if (window.Count == 0) return "";
            if (AvgMs == 0) return "Sem resposta pelo túnel";
            return AvgMs + " ms · osc. " + JitterMs + " ms · perda " + LossPct + "%";
        }

        public static string FormatPct(double pct)
        {
            return pct < 0.05 ? "0%" : pct.ToString(pct < 10 ? "0.#" : "0", CultureInfo.GetCultureInfo("pt-BR")) + "%";
        }
    }
}
