// VPN Panel - Geo (server location lookup) and Latency (on-demand ping).

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
            public int JitterMs;
            public int LossPct;
            public bool ViaTunnel;      // measured while a tunnel was up: other servers' traffic goes through the VPN
            public DateTime At;
            public bool Ok { get { return Ms >= 0; } }
            public int Score { get { return !Ok ? int.MaxValue : Ms + JitterMs * 2 + LossPct * 8; } }
            // For calls and streams a steady path matters more than a short one
            public int LiveScore { get { return !Ok ? int.MaxValue : Ms + JitterMs * 4 + LossPct * 20; } }
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
                    Result measured = Probe(h, 4, 30);
                    ui.BeginInvoke(new Action(() =>
                    {
                        active--;
                        // routing changed meanwhile (connected/disconnected): this number no longer applies
                        if (gen == generation)
                        {
                            pending.Remove(h);
                            measured.ViaTunnel = viaTunnel;
                            measured.At = DateTime.UtcNow;
                            cache[h] = measured;
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

        // Longer burst (20 echoes, ~3 s) for the "best server for live" test: enough samples for jitter and loss
        // to mean something. Blocking; not cached.
        public static Result ProbeStability(string host)
        {
            return Probe(host, 20, 100);
        }

        // Measures "count" echoes when responsive to compute average latency, jitter, and packet loss.
        static Result Probe(string host, int count, int gapMs)
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
                    if (!signaled || dnsEx != null || addrs == null || addrs.Length == 0) return new Result();
                    ip = addrs[0];
                }
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var rtts = new List<int>(count);
                    int sent = 0;
                    for (int i = 0; i < count; i++)
                    {
                        sent++;
                        var rep = ping.Send(ip, i == 0 ? FirstTimeoutMs : SecondTimeoutMs);
                        if (rep != null && rep.Status == System.Net.NetworkInformation.IPStatus.Success)
                        {
                            rtts.Add((int)Math.Max(1, rep.RoundtripTime));
                        }
                        else if (i == 0 && rtts.Count == 0)
                        {
                            return new Result(); // unresponsive/filtered host: fail fast after 1st timeout
                        }
                        if (i < count - 1) Thread.Sleep(gapMs);
                    }
                    if (rtts.Count == 0) return new Result();
                    int sum = 0;
                    for (int i = 0; i < rtts.Count; i++) sum += rtts[i];
                    int avgMs = Math.Max(1, (int)Math.Round(sum / (double)rtts.Count));
                    int jitterSum = 0;
                    for (int i = 1; i < rtts.Count; i++) jitterSum += Math.Abs(rtts[i] - rtts[i - 1]);
                    int jitterMs = rtts.Count > 1 ? (int)Math.Round(jitterSum / (double)(rtts.Count - 1)) : 0;
                    int lossPct = (int)Math.Round((sent - rtts.Count) * 100.0 / sent);
                    return new Result { Ms = avgMs, JitterMs = jitterMs, LossPct = lossPct };
                }
            }
            catch { return new Result(); }
        }
    }
}
