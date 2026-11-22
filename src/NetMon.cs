// VPN Panel - NetMon: in-memory timeline of network events.

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
    // In-memory passive ring buffer recording network flaps, WireSock log errors and tunnel transitions
    static class NetMon
    {
        public enum EventKind { Info, Warn, Error }

        public class EventItem
        {
            public DateTime Time;
            public string Category;
            public EventKind Kind;
            public string Message;
        }

        const int Capacity = 512;
        static readonly object sync = new object();
        static readonly EventItem[] ring = new EventItem[Capacity];
        static int head;
        static int count;
        static long version;

        public static long Version
        {
            get { lock (sync) return version; }
        }

        public static void Record(string category, EventKind kind, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var item = new EventItem
            {
                Time = DateTime.Now,
                Category = string.IsNullOrEmpty(category) ? "Sistema" : category,
                Kind = kind,
                Message = message.Trim()
            };
            lock (sync)
            {
                ring[head] = item;
                head = (head + 1) % Capacity;
                if (count < Capacity) count++;
                version++;
            }
        }

        public static EventItem[] Snapshot(int maxCount)
        {
            lock (sync)
            {
                int take = maxCount <= 0 ? count : Math.Min(maxCount, count);
                var res = new EventItem[take];
                int start = (head - take + Capacity) % Capacity;
                for (int i = 0; i < take; i++)
                    res[i] = ring[(start + i) % Capacity];
                return res;
            }
        }

        public static string ExportText(int maxCount)
        {
            EventItem[] items = Snapshot(maxCount);
            var sb = new StringBuilder();
            for (int i = 0; i < items.Length; i++)
            {
                EventItem it = items[i];
                string tag = it.Kind == EventKind.Error ? "ERRO" : it.Kind == EventKind.Warn ? "AVISO" : "INFO";
                sb.AppendFormat("[{0:HH:mm:ss}] [{1}] [{2}] {3}\r\n", it.Time, tag, it.Category, it.Message);
            }
            return sb.ToString().TrimEnd();
        }

        public static void Clear()
        {
            lock (sync)
            {
                Array.Clear(ring, 0, Capacity);
                head = 0;
                count = 0;
                version++;
            }
        }
    }
}
