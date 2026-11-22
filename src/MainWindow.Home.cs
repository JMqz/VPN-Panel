// Main window: Home tab: connection state, app chips and public IP.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace VpnPanel
{
    partial class MainWindow
    {
        void RefreshRunningAppsAsync(IList<string> apps)
        {
            if (runningAppsScanRunning) return;
            runningAppsScanRunning = true;
            var snapshot = new List<string>(apps);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Process[] all = Process.GetProcesses();
                    for (int i = 0; i < all.Length; i++)
                    {
                        try { names.Add(all[i].ProcessName); } catch { }
                        finally { try { all[i].Dispose(); } catch { } }
                    }
                }
                catch { }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        runningAppsScanRunning = false;
                        if (exiting) return;
                        runningProcNames = names;
                        appScanTime = DateTime.UtcNow;
                        string key = GetRunningAppsKey(snapshot);
                        if (key != lastHomeAppsRunningKey) RenderHomeApps();
                    }));
                }
                else runningAppsScanRunning = false;
            });
        }

        void UpdateState()
        {
            Conn prev = shown;
            Conn st = State;
            shown = st;
            if (st != prev)
            {
                if (st == Conn.On) NetMon.Record("Túnel", NetMon.EventKind.Info, "VPN conectada (" + (active != null ? active.Name : "túnel") + ")");
                else if (st == Conn.NoResponse) NetMon.Record("Túnel", NetMon.EventKind.Warn, "Servidor sem resposta ao handshake");
                else if (st == Conn.Retrying && !waitingForNetworkRestore) NetMon.Record("Túnel", NetMon.EventKind.Warn, "Reconexão automática (tentativa " + Math.Max(1, reconnectAttempts) + "/" + MaxReconnects + ")");
            }
            bool on = st == Conn.On;
            if (on && !Traffic.Running)
            {
                // In Wintun mode, pass null so GetBestInterface(1.1.1.1) tracks the Wintun L3 virtual adapter directly
                Traffic.Start(Tunnel.ActiveEngine == TunnelEngine.Wintun ? null : (active != null ? active.Endpoint : null));
                ResetTrafficLabels();
            }
            if (on && prev != Conn.On)
            {
                HideBanner();
                CheckIp();
                Latency.RouteChanged();
                UpdateAllPingLabels();
                MeasureLatency(false);
            }
            bool busy = st == Conn.Connecting || st == Conn.Retrying || st == Conn.NoResponse;
            UpdateSpinnerAnimation();

            string status, scope;
            if (on)
            {
                txtStatus.Foreground = B("SecureText");
                status = "CONECTADO";
                scope = active == null || active.Mode == AppMode.All ? "Todo o tráfego do PC está protegido"
                      : active.Mode == AppMode.Only ? "Só " + JoinPt(active.Apps) + " passa pela VPN"
                      : "Tudo passa pela VPN, exceto " + JoinPt(active.Apps);
            }
            else
            {
                txtStatus.Foreground = st == Conn.NoResponse ? B("Warn") : B("Text");
                switch (st)
                {
                    case Conn.NoResponse:
                        status = "Sem resposta";
                        scope = "O servidor não está respondendo";
                        break;
                    case Conn.Retrying:
                        if (waitingForNetworkRestore)
                        {
                            status = "Aguardando rede...";
                            scope = "Sem internet física; reconectando assim que voltar";
                        }
                        else
                        {
                            status = "Reconectando...";
                            scope = "A conexão caiu. Tentativa " + Math.Max(1, reconnectAttempts) + " de " + MaxReconnects;
                        }
                        break;
                    case Conn.Connecting:
                        status = "Conectando...";
                        scope = "Falando com o servidor";
                        break;
                    default:
                        status = "Desconectado";
                        scope = "Seu tráfego não está protegido";
                        break;
                }
            }
            txtStatus.Text = status;
            txtScope.Text = scope;
            txtScope.Cursor = on && active != null ? Cursors.Hand : Cursors.Arrow;
            txtScope.ToolTip = on && active != null ? "Clique para conferir quais programas abertos estão passando pela VPN" : null;
            txtScope.TextDecorations = on && active != null && active.Mode != AppMode.All ? TextDecorations.Underline : null;
            txtUptime.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            trafficRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && WindowShowing) UpdateUptimeText();
            bool stoppable = wasRunning == true || busy;
            btnPower.ToolTip = stoppable ? "Desconectar" : "Conectar";

            tray.Icon = on ? trayOn : busy ? trayWarn : trayOff;
            string tip = "VPN Panel: " + status.TrimEnd('.').ToLowerInvariant() + (on && active != null ? " (" + active.Name + ")" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            miStatus.Text = on && active != null ? "Conectado · " + active.Name : status;
            miToggle.Text = stoppable ? "Desconectar" : "Conectar";

            ApplyPowerButtonVisuals(st, busy);
            bool partial = on && active != null && active.Mode == AppMode.Only;
            txtIpNote.Text = partial ? "Este é o IP dos apps fora da VPN. " + JoinPt(active.Apps) + " usa o IP do servidor." : "";
            txtIpNote.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppsSaved();
            UpdateDiagRouteBadge();
        }

        static DateTime appScanTime = DateTime.MinValue;
        static HashSet<string> runningProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool IsAppRunning(string appName)
        {
            if (string.IsNullOrEmpty(appName)) return false;
            string clean = appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? appName.Substring(0, appName.Length - 4)
                : appName;
            DateTime now = DateTime.UtcNow;
            if ((now - appScanTime).TotalSeconds >= 2.5)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Process[] all = Process.GetProcesses();
                    for (int i = 0; i < all.Length; i++)
                    {
                        try { names.Add(all[i].ProcessName); } catch { }
                        finally { try { all[i].Dispose(); } catch { } }
                    }
                }
                catch { }
                runningProcNames = names;
                appScanTime = now;
            }
            return runningProcNames.Contains(clean);
        }

        static string GetRunningAppsKey(IList<string> apps)
        {
            if (apps == null || apps.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < apps.Count; i++)
            {
                sb.Append(apps[i]).Append(':').Append(IsAppRunning(apps[i]) ? '1' : '0').Append(';');
            }
            return sb.ToString();
        }

        readonly Dictionary<string, Color> appColorCache = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        void GetAppBrandColors(string appName, ImageSource ico, out Brush bgBrush, out Brush fgBrush)
        {
            Color baseColor;
            if (!appColorCache.TryGetValue(appName, out baseColor) || (baseColor.R == 147 && baseColor.G == 112 && baseColor.B == 245 && ico != null))
            {
                baseColor = ExtractDominantAppColor(appName, ico);
                appColorCache[appName] = baseColor;
            }
            byte fgR = (byte)Math.Min(255, baseColor.R + (255 - baseColor.R) * 38 / 100);
            byte fgG = (byte)Math.Min(255, baseColor.G + (255 - baseColor.G) * 38 / 100);
            byte fgB = (byte)Math.Min(255, baseColor.B + (255 - baseColor.B) * 38 / 100);
            var bg = new SolidColorBrush(Color.FromArgb(50, baseColor.R, baseColor.G, baseColor.B));
            var fg = new SolidColorBrush(Color.FromRgb(fgR, fgG, fgB));
            bg.Freeze();
            fg.Freeze();
            bgBrush = bg;
            fgBrush = fg;
        }

        static Color ExtractDominantAppColor(string appName, ImageSource ico)
        {
            var bmp = ico as BitmapSource;
            if (bmp != null)
            {
                try
                {
                    var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                    int w = conv.PixelWidth, h = conv.PixelHeight;
                    if (w > 0 && h > 0)
                    {
                        byte[] px = new byte[w * h * 4];
                        conv.CopyPixels(px, w * 4, 0);
                        double rSum = 0, gSum = 0, bSum = 0, wSum = 0;
                        for (int i = 0; i < px.Length; i += 4)
                        {
                            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                            if (a < 100) continue;
                            int max = Math.Max(r, Math.Max(g, b));
                            int min = Math.Min(r, Math.Min(g, b));
                            int delta = max - min;
                            if (max < 42) continue;
                            if (min > 215 && delta < 25) continue;
                            double weight = (delta + 12.0) * (max / 255.0) * (a / 255.0);
                            rSum += r * weight;
                            gSum += g * weight;
                            bSum += b * weight;
                            wSum += weight;
                        }
                        if (wSum > 0)
                        {
                            return Color.FromRgb(
                                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(rSum / wSum))),
                                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(gSum / wSum))),
                                (byte)Math.Max(0, Math.Min(255, (int)Math.Round(bSum / wSum)))
                            );
                        }
                    }
                }
                catch { }
            }
            string lower = (appName ?? "").ToLowerInvariant();
            if (lower.Contains("discord")) return Color.FromRgb(88, 101, 242);
            if (lower.Contains("spotify")) return Color.FromRgb(29, 185, 84);
            if (lower.Contains("steam")) return Color.FromRgb(59, 156, 255);
            if (lower.Contains("chrome")) return Color.FromRgb(66, 133, 244);
            if (lower.Contains("firefox")) return Color.FromRgb(255, 113, 57);
            if (lower.Contains("brave")) return Color.FromRgb(251, 84, 43);
            if (lower.Contains("opera")) return Color.FromRgb(255, 27, 45);
            if (lower.Contains("edge")) return Color.FromRgb(0, 120, 215);
            return Color.FromRgb(147, 112, 245);
        }

        void RenderHomeApps()
        {
            if (homeAppsPanel == null) return;
            AppMode mode = AppMode.All;
            IList<string> apps = null;
            bool on = shown == Conn.On;
            if (on && active != null)
            {
                mode = active.Mode;
                apps = active.Apps;
            }
            else if (settings.SharedApps)
            {
                mode = settings.AppsMode;
                apps = settings.Apps;
            }
            else if (active != null)
            {
                mode = active.Mode;
                apps = active.Apps;
            }

            if (!on || mode == AppMode.All || apps == null || apps.Count == 0)
            {
                txtScope.Visibility = Visibility.Visible;
                homeAppsPanel.Visibility = Visibility.Collapsed;
                homeAppsPanel.Children.Clear();
                lastHomeAppsRunningKey = "";
                return;
            }

            txtScope.Visibility = Visibility.Collapsed;
            if (!WindowShowing) return;
            string runKey = (int)mode + "|" + GetRunningAppsKey(apps);
            lastHomeAppsCheck = DateTime.UtcNow;
            if (runKey == lastHomeAppsRunningKey && homeAppsPanel.Visibility == Visibility.Visible && homeAppsPanel.Children.Count > 0)
                return;
            lastHomeAppsRunningKey = runKey;
            homeAppsPanel.Children.Clear();
            int maxPills = 4;
            int count = Math.Min(apps.Count, maxPills);
            for (int i = 0; i < count; i++)
            {
                string appName = apps[i];
                bool isRunning = IsAppRunning(appName);
                ImageSource ico = AppIcon(appName, null);
                Brush appBg, appFg;
                GetAppBrandColors(appName, ico, out appBg, out appFg);
                var pill = new Border
                {
                    Background = appBg,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(10, 4, 11, 4),
                    Margin = new Thickness(3, 2, 3, 2),
                    Opacity = isRunning ? 1.0 : 0.88,
                    Cursor = Cursors.Hand,
                    ToolTip = isRunning
                        ? appName + " está aberto e ativo · Clique para conferir o roteamento em tempo real"
                        : appName + " está fechado no momento · Assim que abrir, passará pela VPN"
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (ico != null)
                {
                    sp.Children.Add(new Image
                    {
                        Source = ico,
                        Width = 14,
                        Height = 14,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                else
                {
                    var avatar = new Border
                    {
                        Width = 14,
                        Height = 14,
                        CornerRadius = new CornerRadius(7),
                        Background = appFg,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    string letter = string.IsNullOrEmpty(appName) ? "A" : appName.Substring(0, 1).ToUpperInvariant();
                    avatar.Child = new TextBlock
                    {
                        Text = letter,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        Foreground = B("Bg"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    sp.Children.Add(avatar);
                }
                sp.Children.Add(new TextBlock
                {
                    Text = mode == AppMode.Except ? "Sem " + appName : appName,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = appFg,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var fgColor = ((SolidColorBrush)appFg).Color;
                var dotBrush = isRunning
                    ? appFg
                    : new SolidColorBrush(Color.FromArgb(130, fgColor.R, fgColor.G, fgColor.B));
                if (!isRunning) dotBrush.Freeze();
                sp.Children.Add(new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = dotBrush,
                    Margin = new Thickness(6, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                pill.Child = sp;
                pill.MouseLeftButtonUp += (s, e) =>
                {
                    if (shown == Conn.On && active != null)
                        Dialogs.VerifyRouting(w, active, Tunnel.Ipv6Isolated, AppIcon, appsNeedReconnect);
                };
                homeAppsPanel.Children.Add(pill);
            }
            if (apps.Count > maxPills)
            {
                var morePill = new Border
                {
                    Background = B("Surface2"),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(3, 2, 3, 2),
                    Cursor = Cursors.Hand
                };
                morePill.Child = new TextBlock
                {
                    Text = "+" + (apps.Count - maxPills),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = B("Muted")
                };
                morePill.MouseLeftButtonUp += (s, e) =>
                {
                    if (shown == Conn.On && active != null)
                        Dialogs.VerifyRouting(w, active, Tunnel.Ipv6Isolated, AppIcon, appsNeedReconnect);
                    else
                        tabApps.IsChecked = true;
                };
                homeAppsPanel.Children.Add(morePill);
            }
            homeAppsPanel.Visibility = Visibility.Visible;
        }

        void CheckIp()
        {
            if (!WindowShowing)
            {
                ipDirty = true;
                return;
            }
            ipDirty = false;
            if (ipClient != null)
            {
                try { ipClient.CancelAsync(); ipClient.Dispose(); } catch { }
                ipClient = null;
            }
            txtIp.Text = "...";
            txtLoc.Text = "...";
            txtOrg.Text = "...";
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            ipClient = wc;
            wc.Headers["User-Agent"] = "vpn-panel";
            wc.DownloadStringCompleted += (s, e) =>
            {
                wc.Dispose();
                if (ipClient == wc) ipClient = null;
                if (e.Cancelled) return;
                try
                {
                    if (e.Error != null) throw e.Error;
                    var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(e.Result);
                    Func<string, string> get = k => { object v; return j.TryGetValue(k, out v) ? Convert.ToString(v) : ""; };
                    txtIp.Text = get("ip");
                    var place = new Geo.Place { Country = get("country"), City = get("city") };
                    txtLoc.Text = place.City.Length > 0 ? place.City + ", " + place.CountryName : place.CountryName;
                    // "AS53006 ALGAR TELECOM S/A" -> "ALGAR TELECOM S/A"
                    string org = get("org");
                    txtOrg.Text = Regex.Replace(org, @"^AS\d+\s+", "");
                    txtOrg.ToolTip = org;
                }
                catch
                {
                    txtIp.Text = "Sem resposta";
                    txtLoc.Text = "—";
                    txtOrg.Text = "—";
                }
            };
            wc.DownloadStringAsync(new Uri("https://ipinfo.io/json"));
        }

        // ---------- servers ----------
    }
}
