// VPN Panel - Traffic: byte counters of the physical network card.

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
}
