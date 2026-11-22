// VPN Panel - Updater: GitHub release check, download and verification.

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
    static class Updater
    {
        public class ReleaseInfo
        {
            public string Version;
            public string HtmlUrl;
            public string DownloadUrl;
            public string Notes;
            public string SetupDigest;
            public string AppDigest;
            public bool IsNewer;
            public bool SameVersionUpdated;
        }

        const string ApiUrl = "https://api.github.com/repos/JMqz/VPN-Panel/releases/latest";

        static Version ParseVersion(string ver)
        {
            if (string.IsNullOrEmpty(ver)) return null;
            string clean = Regex.Replace(ver.Trim(), @"^[^\d]*", "");
            if (string.IsNullOrEmpty(clean)) return null;
            try
            {
                return new Version(clean.Contains(".") ? clean : clean + ".0");
            }
            catch
            {
                return null;
            }
        }

        public static bool IsNewer(string remoteTag, string currentVer)
        {
            Version r = ParseVersion(remoteTag);
            Version c = ParseVersion(currentVer);
            return r != null && c != null && r > c;
        }

        public static bool IsSameVersion(string remoteTag, string currentVer)
        {
            Version r = ParseVersion(remoteTag);
            Version c = ParseVersion(currentVer);
            return r != null && c != null && r == c;
        }

        public static string FileSha256(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder("sha256:", 7 + hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        static string cachedLocalExeDigest;

        public static bool DigestMatches(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
            string a = actual.Trim(), e = expected.Trim();
            if (!e.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
            return a.Equals(e, StringComparison.OrdinalIgnoreCase);
        }

        // Checks a downloaded installer against the SHA-256 that GitHub publishes for the release file.
        // Returns null when it matches, otherwise the message to show. Without a published digest
        // nothing can be checked, so the file is refused instead of being run with admin rights.
        public static string VerifyDownload(string path, string expectedDigest)
        {
            if (string.IsNullOrWhiteSpace(expectedDigest))
                return "A release não informou o código de verificação (SHA-256) do instalador. Baixe a atualização pela página do projeto.";
            string actual = FileSha256(path);
            if (actual == null) return "Não foi possível ler o instalador baixado.";
            if (!DigestMatches(actual, expectedDigest))
                return "O instalador baixado não confere com o publicado na release (SHA-256 diferente). Ele foi descartado.";
            return null;
        }

        public static void Check(Action<ReleaseInfo, Exception> callback)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // Tls12 + Tls13
                    var req = (HttpWebRequest)WebRequest.Create(ApiUrl + "?t=" + DateTime.UtcNow.Ticks);
                    req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                    req.Headers["Cache-Control"] = "no-cache, no-store";
                    req.Headers["Pragma"] = "no-cache";
                    req.UserAgent = "vpn-panel";
                    req.Timeout = 10000;
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                        string tag = d.ContainsKey("tag_name") ? Convert.ToString(d["tag_name"]) : "";
                        string html = d.ContainsKey("html_url") ? Convert.ToString(d["html_url"]) : "";
                        string body = d.ContainsKey("body") ? Convert.ToString(d["body"]) : "";
                        string downloadUrl = null;
                        string setupDigest = null;
                        string appDigest = null;
                        var allAppDigests = new List<string>();

                        object assetsObj;
                        if (d.TryGetValue("assets", out assetsObj) && assetsObj is System.Collections.IEnumerable)
                        {
                            foreach (object item in (System.Collections.IEnumerable)assetsObj)
                            {
                                var asset = item as Dictionary<string, object>;
                                if (asset != null && asset.ContainsKey("name") && asset.ContainsKey("browser_download_url"))
                                {
                                    string name = Convert.ToString(asset["name"]);
                                    string url = Convert.ToString(asset["browser_download_url"]);
                                    string digest = asset.ContainsKey("digest") && asset["digest"] != null ? Convert.ToString(asset["digest"]) : null;
                                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                                    if (name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (downloadUrl == null || name.StartsWith("VPN-Panel-Setup-", StringComparison.OrdinalIgnoreCase))
                                        {
                                            downloadUrl = url;
                                            setupDigest = digest;
                                        }
                                    }
                                    else
                                    {
                                        if (!string.IsNullOrEmpty(digest)) allAppDigests.Add(digest);
                                        if (appDigest == null || name.IndexOf("portatil", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            appDigest = digest;
                                        }
                                    }
                                }
                            }
                        }

                        bool versionNewer = IsNewer(tag, Program.Version);
                        bool sameVerUpdated = false;
                        if (!versionNewer && IsSameVersion(tag, Program.Version) && (!string.IsNullOrEmpty(appDigest) || !string.IsNullOrEmpty(setupDigest)))
                        {
                            if (cachedLocalExeDigest == null) cachedLocalExeDigest = FileSha256(Paths.Exe);
                            string localDigest = cachedLocalExeDigest;
                            string savedDigest = Settings.Load().InstalledDigest;
                            bool matchesLocalExe = !string.IsNullOrEmpty(localDigest) &&
                                (!string.IsNullOrEmpty(appDigest)
                                    ? string.Equals(localDigest, appDigest, StringComparison.OrdinalIgnoreCase)
                                    : allAppDigests.Any(x => string.Equals(x, localDigest, StringComparison.OrdinalIgnoreCase)));
                            bool matchesSavedSetup = !string.IsNullOrEmpty(savedDigest) &&
                                ((!string.IsNullOrEmpty(setupDigest) && string.Equals(savedDigest, setupDigest, StringComparison.OrdinalIgnoreCase)) ||
                                 (!string.IsNullOrEmpty(appDigest) && string.Equals(savedDigest, appDigest, StringComparison.OrdinalIgnoreCase)));

                            if (!matchesLocalExe && !matchesSavedSetup)
                            {
                                sameVerUpdated = true;
                            }
                        }

                        var info = new ReleaseInfo
                        {
                            Version = Regex.Replace(tag, @"^[^\d]*", ""),
                            HtmlUrl = html,
                            DownloadUrl = downloadUrl,
                            Notes = body,
                            SetupDigest = setupDigest,
                            AppDigest = appDigest,
                            IsNewer = versionNewer || sameVerUpdated,
                            SameVersionUpdated = sameVerUpdated
                        };

                        var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                        if (callback != null)
                        {
                            if (ui != null && !ui.CheckAccess()) ui.BeginInvoke(new Action(() => callback(info, null)));
                            else callback(info, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (callback != null)
                    {
                        if (ui != null && !ui.CheckAccess()) ui.BeginInvoke(new Action(() => callback(null, ex)));
                        else callback(null, ex);
                    }
                }
            });
        }
    }
}
