// Main window: tray menu items, power button visuals, accent colors and icons.

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
        // Recent servers and the "Todos os servidores" submenu, rebuilt only when something they show changed
        void UpdateTrayServers()
        {
            string[] names;
            try { names = Paths.ConfigNames(); } catch { names = new string[0]; }
            string current = active != null ? active.Name : "";
            string live = wasRunning == true && runningConfig != null ? runningConfig : "";
            string key = Paths.ConfigsVersion + "|" + Geo.Version + "|" + current + "|" + live + "|" + string.Join("|", settings.Recent);
            if (key == trayMenuKey) return;
            trayMenuKey = key;

            foreach (WF.ToolStripItem item in miRecent)
            {
                trayMenu.Items.Remove(item);
                item.Dispose();
            }
            miRecent.Clear();
            var exists = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var recent = settings.Recent.Where(exists.Contains).Take(TrayRecentCount).ToList();
            // never connected yet: the selected server still gets a one-click entry
            if (recent.Count == 0 && active != null) recent.Add(active.Name);
            int at = trayMenu.Items.IndexOf(miAllServers);
            if (recent.Count > 0)
            {
                var header = new WF.ToolStripMenuItem("Recentes") { Enabled = false, Padding = TrayPadding };
                trayMenu.Items.Insert(at++, header);
                miRecent.Add(header);
                foreach (string n in recent)
                {
                    WF.ToolStripMenuItem item = TrayServerItem(n, current, live);
                    trayMenu.Items.Insert(at++, item);
                    miRecent.Add(item);
                }
            }

            WF.ToolStripItemCollection all = miAllServers.DropDownItems;
            while (all.Count > 0)
            {
                WF.ToolStripItem old = all[0];
                all.RemoveAt(0);
                old.Dispose();
            }
            foreach (string n in names) all.Add(TrayServerItem(n, current, live));
            miAllServers.Text = "Todos os servidores (" + names.Length + ")";
            miAllServers.Visible = names.Length > 0;
            miServersEnd.Visible = names.Length > 0;
        }

        WF.ToolStripMenuItem TrayServerItem(string name, string current, string live)
        {
            bool isLive = live.Equals(name, StringComparison.OrdinalIgnoreCase);
            bool isCurrent = current.Equals(name, StringComparison.OrdinalIgnoreCase);
            ConfigInfo c = ConfigInfo.Read(name);
            string code = c.Readable ? c.CountryCode : null;
            // "&" would turn into a mnemonic underline
            string text = name.Replace("&", "&&") + (code != null ? "   " + code : "");
            var item = new WF.ToolStripMenuItem(text) { Checked = isCurrent, Padding = TrayPadding };
            item.ToolTipText = isLive ? "Conectado" : isCurrent ? "Selecionado. Clique para conectar" : "Conectar a este servidor";
            string target = name;
            item.Click += (s, e) => SwitchServer(target);
            return item;
        }

        void ApplyPowerButtonVisuals(Conn st, bool isBusy)
        {
            if (statusRing == null || shieldBg == null) return;

            bool wantSpin = isBusy || st == Conn.Connecting;
            if (wantSpin)
            {
                statusRing.Stroke = B("Line");
                statusSpinner.Visibility = Visibility.Visible;
                shieldBg.Stroke = B("Warn");
                ravenBeak.Fill = B("Warn");
                ravenHead.Fill = B("Warn");
                ravenBody.Fill = B("Warn");
                ravenTail.Fill = B("Warn");
                glow.Visibility = Visibility.Visible;
                glow.Opacity = 0.5;
                if (spinnerRotate != null && !powerSpinnerRunning)
                {
                    powerSpinnerRunning = true;
                    spinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
                }
            }
            else if (st == Conn.On)
            {
                if (spinnerRotate != null && powerSpinnerRunning)
                {
                    powerSpinnerRunning = false;
                    spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                }
                statusSpinner.Visibility = Visibility.Collapsed;
                statusRing.Stroke = B("Secure");
                shieldBg.Stroke = B("Secure");
                ravenBeak.Fill = B("SecureText");
                ravenHead.Fill = B("SecureText");
                ravenBody.Fill = B("SecureText");
                ravenTail.Fill = B("SecureText");
                glow.Visibility = Visibility.Visible;
                glow.Opacity = 1.0;
            }
            else
            {
                if (spinnerRotate != null && powerSpinnerRunning)
                {
                    powerSpinnerRunning = false;
                    spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                }
                statusSpinner.Visibility = Visibility.Collapsed;
                statusRing.Stroke = B("Line");
                shieldBg.Stroke = B("Line");
                ravenBeak.Fill = B("Muted");
                ravenHead.Fill = B("Muted");
                ravenBody.Fill = B("Muted");
                ravenTail.Fill = B("Muted");
                glow.Visibility = Visibility.Collapsed;
                glow.Opacity = 0;
            }
        }

        static string CountryFlag(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 2) return null;
            int c1 = char.ToUpperInvariant(code[0]);
            int c2 = char.ToUpperInvariant(code[1]);
            if (c1 < 'A' || c1 > 'Z' || c2 < 'A' || c2 > 'Z') return null;
            return char.ConvertFromUtf32(0x1F1E6 + (c1 - 'A')) + char.ConvertFromUtf32(0x1F1E6 + (c2 - 'A'));
        }

        class ColorThemePreset
        {
            public string Id;
            public string Name;
            public System.Windows.Media.Color Primary;
            public System.Windows.Media.Color Text;
            public System.Windows.Media.Color Soft;
            public SD.Color GdiPrimary;
        }

        static readonly ColorThemePreset[] ColorPresets = new ColorThemePreset[]
        {
            new ColorThemePreset {
                Id = "green",
                Name = "Verde Esmeralda",
                Primary = System.Windows.Media.Color.FromRgb(68, 173, 77),
                Text = System.Windows.Media.Color.FromRgb(91, 208, 104),
                Soft = System.Windows.Media.Color.FromArgb(36, 68, 173, 77),
                GdiPrimary = SD.Color.FromArgb(68, 173, 77)
            },
            new ColorThemePreset {
                Id = "cyan",
                Name = "Ciano Neon",
                Primary = System.Windows.Media.Color.FromRgb(6, 182, 212),
                Text = System.Windows.Media.Color.FromRgb(34, 211, 238),
                Soft = System.Windows.Media.Color.FromArgb(36, 6, 182, 212),
                GdiPrimary = SD.Color.FromArgb(6, 182, 212)
            },
            new ColorThemePreset {
                Id = "blue",
                Name = "Azul Elétrico",
                Primary = System.Windows.Media.Color.FromRgb(59, 130, 246),
                Text = System.Windows.Media.Color.FromRgb(96, 165, 250),
                Soft = System.Windows.Media.Color.FromArgb(36, 59, 130, 246),
                GdiPrimary = SD.Color.FromArgb(59, 130, 246)
            },
            new ColorThemePreset {
                Id = "purple",
                Name = "Violeta",
                Primary = System.Windows.Media.Color.FromRgb(139, 92, 246),
                Text = System.Windows.Media.Color.FromRgb(167, 139, 250),
                Soft = System.Windows.Media.Color.FromArgb(36, 139, 92, 246),
                GdiPrimary = SD.Color.FromArgb(139, 92, 246)
            },
            new ColorThemePreset {
                Id = "amber",
                Name = "Laranja Âmbar",
                Primary = System.Windows.Media.Color.FromRgb(245, 158, 11),
                Text = System.Windows.Media.Color.FromRgb(251, 191, 36),
                Soft = System.Windows.Media.Color.FromArgb(36, 245, 158, 11),
                GdiPrimary = SD.Color.FromArgb(245, 158, 11)
            },
            new ColorThemePreset {
                Id = "rose",
                Name = "Rosa Choque",
                Primary = System.Windows.Media.Color.FromRgb(236, 72, 153),
                Text = System.Windows.Media.Color.FromRgb(244, 114, 182),
                Soft = System.Windows.Media.Color.FromArgb(36, 236, 72, 153),
                GdiPrimary = SD.Color.FromArgb(236, 72, 153)
            }
        };

        void ApplyAccentColor(string presetId, bool saveSettings = true)
        {
            ColorThemePreset preset = null;
            if (!string.IsNullOrEmpty(presetId))
            {
                foreach (var p in ColorPresets)
                {
                    if (p.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase)) { preset = p; break; }
                }
            }
            if (preset == null) preset = ColorPresets[0];
            activePreset = preset;
            activeGdiAccent = preset.GdiPrimary;

            var primaryBrush = new SolidColorBrush(preset.Primary); primaryBrush.Freeze();
            var textBrush = new SolidColorBrush(preset.Text); textBrush.Freeze();
            var softBrush = new SolidColorBrush(preset.Soft); softBrush.Freeze();

            Application.Current.Resources["Accent"] = primaryBrush;
            Application.Current.Resources["AccentSoft"] = softBrush;
            Application.Current.Resources["Secure"] = primaryBrush;
            Application.Current.Resources["SecureText"] = textBrush;
            Application.Current.Resources["SecureSoft"] = softBrush;

            if (glow != null)
            {
                var glowBrush = new RadialGradientBrush();
                glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x35, preset.Primary.R, preset.Primary.G, preset.Primary.B), 0.0));
                glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, preset.Primary.R, preset.Primary.G, preset.Primary.B), 0.6));
                glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 16, 28, 44), 1.0));
                glowBrush.Freeze();
                glow.Fill = glowBrush;
            }

            var oldOn = trayOn;
            var oldOff = trayOff;
            var oldWarn = trayWarn;

            trayOn = TrayIcon(true, false, preset.GdiPrimary);
            trayOff = TrayIcon(false, false, preset.GdiPrimary);
            trayWarn = TrayIcon(true, true, preset.GdiPrimary);

            if (tray != null)
            {
                tray.Icon = shown == Conn.On ? trayOn : (shown == Conn.Connecting || shown == Conn.Retrying || shown == Conn.NoResponse) ? trayWarn : trayOff;
            }

            if (oldOn != null) { try { oldOn.Dispose(); } catch { } }
            if (oldOff != null) { try { oldOff.Dispose(); } catch { } }
            if (oldWarn != null) { try { oldWarn.Dispose(); } catch { } }

            bool isBusy = shown == Conn.Connecting || shown == Conn.Retrying || shown == Conn.NoResponse;
            ApplyPowerButtonVisuals(shown, isBusy);
            if (shown == Conn.On) txtStatus.Foreground = textBrush;
            if (txtDownIcon != null) txtDownIcon.Foreground = textBrush;
            if (titleIcon != null)
            {
                try
                {
                    byte[] icoBytes = BuildAccentIcoBytes(preset.GdiPrimary);
                    using (var ms = new MemoryStream(icoBytes))
                    {
                        var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var frame32 = decoder.Frames.FirstOrDefault(f => f.PixelWidth == 32) ?? decoder.Frames[0];
                        var frame48 = decoder.Frames.FirstOrDefault(f => f.PixelWidth == 48) ?? frame32;
                        titleIcon.Source = frame32;
                        w.Icon = frame48;
                    }
                    ApplyTaskbarIcon(preset, icoBytes, saveSettings);
                }
                catch { }
            }
            else
            {
                try { ApplyTaskbarIcon(preset, null, saveSettings); } catch { }
            }
            UpdateTraffic();

            if (ServersShowing) RenderServerList();
            else serversDirty = true;
            RenderHomeApps();

            if (saveSettings && !string.Equals(settings.AccentColor, preset.Id, StringComparison.OrdinalIgnoreCase))
            {
                settings.AccentColor = preset.Id;
                settings.Save();
            }

            UpdateColorSwatches();
        }

        void InitColorSwatches()
        {
            if (panelColorPresets == null) return;
            panelColorPresets.Children.Clear();
            string currentId = activePreset != null ? activePreset.Id : (settings.AccentColor ?? "green");

            foreach (var preset in ColorPresets)
            {
                var p = preset;
                bool isSelected = string.Equals(p.Id, currentId, StringComparison.OrdinalIgnoreCase);

                var swatch = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(17),
                    Background = new SolidColorBrush(p.Primary),
                    Margin = new Thickness(0, 0, 12, 8),
                    Cursor = Cursors.Hand,
                    ToolTip = p.Name,
                    BorderThickness = new Thickness(isSelected ? 2.5 : 1),
                    BorderBrush = isSelected ? Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255)),
                    Tag = p.Id
                };

                var check = new TextBlock
                {
                    Text = "\uE73E",
                    FontFamily = (FontFamily)Application.Current.FindResource("Icons"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed
                };
                swatch.Child = check;

                swatch.MouseLeftButtonUp += (s, e) => ApplyAccentColor(p.Id, true);
                panelColorPresets.Children.Add(swatch);
            }
        }

        void UpdateColorSwatches()
        {
            if (panelColorPresets == null) return;
            string currentId = activePreset != null ? activePreset.Id : (settings.AccentColor ?? "green");
            foreach (UIElement child in panelColorPresets.Children)
            {
                var border = child as Border;
                if (border == null) continue;
                string id = border.Tag as string;
                bool selected = string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase);
                border.BorderThickness = new Thickness(selected ? 2.5 : 1);
                border.BorderBrush = selected ? Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
                var check = border.Child as TextBlock;
                if (check != null)
                {
                    check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        static void SetClassLongPtrSafe(IntPtr hwnd, int index, IntPtr value)
        {
            if (IntPtr.Size == 8) SetClassLongPtr64(hwnd, index, value);
            else SetClassLong32(hwnd, index, value.ToInt32());
        }

        static void SetWindowStringProp(IPropertyStore store, PropertyKey key, string value)
        {
            var pv = new PropVariant { vt = 31, ptrVal = Marshal.StringToCoTaskMemUni(value ?? "") };
            try { store.SetValue(ref key, ref pv); }
            finally { PropVariantClear(ref pv); }
        }

        static string EnsureAccentIcoFile(ColorThemePreset preset, byte[] icoBytes)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPN Panel", "icons");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "accent-" + preset.Id.ToLowerInvariant() + "-v1.ico");
                if (File.Exists(path) && new FileInfo(path).Length == icoBytes.Length) return path;
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, icoBytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return path;
            }
            catch { return null; }
        }

        void ApplyTaskbarIcon(ColorThemePreset preset, byte[] icoBytes, bool allowTabRefresh)
        {
            if (windowHandle == IntPtr.Zero || preset == null) return;
            if (icoBytes == null) icoBytes = BuildAccentIcoBytes(preset.GdiPrimary);

            IntPtr newBig = IntPtr.Zero, newSmall = IntPtr.Zero;
            try
            {
                using (var bigBmp = RenderMedallionBitmap(48, true, false, preset.GdiPrimary))
                    newBig = bigBmp.GetHicon();
                int smallSz = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
                using (var smallBmp = RenderMedallionBitmap(smallSz, true, false, preset.GdiPrimary))
                    newSmall = smallBmp.GetHicon();

                SetClassLongPtrSafe(windowHandle, GclpHicon, newBig);
                SetClassLongPtrSafe(windowHandle, GclpHiconSm, newSmall);
                SendMessage(windowHandle, WmSetIcon, (IntPtr)IconBig, newBig);
                SendMessage(windowHandle, WmSetIcon, (IntPtr)IconSmall, newSmall);

                IntPtr prevBig = hTaskbarBigIcon;
                IntPtr prevSmall = hTaskbarSmallIcon;
                hTaskbarBigIcon = newBig;
                hTaskbarSmallIcon = newSmall;
                newBig = IntPtr.Zero;
                newSmall = IntPtr.Zero;
                if (prevBig != IntPtr.Zero) DestroyIcon(prevBig);
                if (prevSmall != IntPtr.Zero) DestroyIcon(prevSmall);
            }
            catch
            {
                if (newBig != IntPtr.Zero) DestroyIcon(newBig);
                if (newSmall != IntPtr.Zero) DestroyIcon(newSmall);
            }

            string icoPath = EnsureAccentIcoFile(preset, icoBytes);
            try
            {
                Guid iid = IidPropertyStore;
                IPropertyStore store;
                if (SHGetPropertyStoreForWindow(windowHandle, ref iid, out store) == 0 && store != null)
                {
                    try
                    {
                        SetWindowStringProp(store, PkeyAppUserModelId, "JMqz.VpnPanel." + preset.Id);
                        if (!string.IsNullOrEmpty(icoPath))
                            SetWindowStringProp(store, PkeyAppUserModelRelaunchIcon, icoPath + ",0");
                        SetWindowStringProp(store, PkeyAppUserModelRelaunchName, "VPN Panel");
                        SetWindowStringProp(store, PkeyAppUserModelRelaunchCmd, "\"" + Paths.Exe + "\"");
                        store.Commit();
                    }
                    finally { Marshal.ReleaseComObject(store); }
                }
            }
            catch { }

            if (allowTabRefresh && w.IsVisible && w.ShowInTaskbar &&
                !string.Equals(lastTaskbarAccentId, preset.Id, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var tbl = (ITaskbarList)new ComTaskbarList();
                    try
                    {
                        tbl.HrInit();
                        tbl.DeleteTab(windowHandle);
                        tbl.AddTab(windowHandle);
                    }
                    finally { Marshal.ReleaseComObject(tbl); }
                }
                catch { }
            }
            lastTaskbarAccentId = preset.Id;
        }

        static byte[] BuildAccentIcoBytes(SD.Color customAccent)
        {
            int[] sizes = new[] { 16, 20, 24, 32, 48, 64 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (var bmp = RenderMedallionBitmap(sizes[i], true, false, customAccent))
                using (var pngMs = new MemoryStream())
                {
                    bmp.Save(pngMs, SD.Imaging.ImageFormat.Png);
                    pngs[i] = pngMs.ToArray();
                }
            }
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write((byte)(sizes[i] % 256));
                    bw.Write((byte)(sizes[i] % 256));
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((ushort)1);
                    bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length);
                    bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                for (int i = 0; i < pngs.Length; i++) bw.Write(pngs[i]);
                bw.Flush();
                return ms.ToArray();
            }
        }

        // Cyber Shield Raven icon (Mullvad style: solid accent circle with dark navy raven cutout for maximum clarity)
        static SD.Bitmap RenderMedallionBitmap(int s, bool active, bool warn, SD.Color? customAccent)
        {
            var bmp = new SD.Bitmap(s, s);
            using (SD.Graphics g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(SD.Color.Transparent);

                float inset = s <= 20 ? 0.5f : 1.0f;
                if (warn)
                {
                    using (var bg = new SD.SolidBrush(SD.Color.FromArgb(255, 213, 36)))
                        g.FillEllipse(bg, inset, inset, s - 2 * inset, s - 2 * inset);

                    float cx = s / 2.0F, cy = s / 2.0F;
                    g.TranslateTransform(cx - 0.5f, cy);
                    using (var dark = new SD.SolidBrush(SD.Color.FromArgb(16, 28, 44)))
                        DrawRavenShape(g, dark, dark, s / 115.0F);
                }
                else if (active)
                {
                    SD.Color baseCol = customAccent ?? SD.Color.FromArgb(68, 173, 77);
                    using (var bg = new SD.SolidBrush(baseCol))
                        g.FillEllipse(bg, inset, inset, s - 2 * inset, s - 2 * inset);

                    float cx = s / 2.0F, cy = s / 2.0F;
                    g.TranslateTransform(cx - 0.5f, cy);
                    using (var dark = new SD.SolidBrush(SD.Color.FromArgb(16, 28, 44)))
                        DrawRavenShape(g, dark, dark, s / 115.0F);
                }
                else
                {
                    using (var bg = new SD.SolidBrush(SD.Color.FromArgb(20, 30, 46)))
                        g.FillEllipse(bg, inset, inset, s - 2 * inset, s - 2 * inset);

                    float penWidth = s <= 20 ? 1.2f : Math.Max(1.5f, s / 20.0f);
                    using (var ringPen = new SD.Pen(SD.Color.FromArgb(60, 75, 96), penWidth))
                        g.DrawEllipse(ringPen, inset, inset, s - 2 * inset, s - 2 * inset);

                    float cx = s / 2.0F, cy = s / 2.0F;
                    g.TranslateTransform(cx - 0.5f, cy);
                    using (var muted = new SD.SolidBrush(SD.Color.FromArgb(120, 138, 162)))
                        DrawRavenShape(g, muted, muted, s / 125.0F);
                }
            }
            return bmp;
        }

        static SD.Icon TrayIcon(bool active, bool warn, SD.Color? customAccent = null, int? targetSize = null)
        {
            int s = targetSize.HasValue ? targetSize.Value : Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
            using (var bmp = RenderMedallionBitmap(s, active, warn, customAccent))
            {
                IntPtr hIcon = bmp.GetHicon();
                try { return (SD.Icon)SD.Icon.FromHandle(hIcon).Clone(); }
                finally { DestroyIcon(hIcon); }
            }
        }

        static void DrawRavenShape(SD.Graphics g, SD.Brush bHead, SD.Brush bTail, float scale)
        {
            var st = g.Save();
            if (Math.Abs(scale - 1.0F) > 0.001F)
                g.ScaleTransform(scale, scale);

            using (var bp = new SD.Drawing2D.GraphicsPath())
            {
                bp.AddBezier(9, -24, 19, -24, 27, -21, 31, -15);
                bp.AddBezier(31, -15, 22, -14, 15, -14, 9, -15);
                bp.CloseFigure();
                g.FillPath(bHead, bp);
            }

            using (var hp = new SD.Drawing2D.GraphicsPath())
            {
                hp.AddBezier(-16, -10, -14, -26, -5, -31, 5, -29);
                hp.AddBezier(5, -29, 11, -28, 12, -21, 9, -17);
                hp.AddBezier(9, -17, 8, -12, 10, -8, 12, -5);
                hp.AddBezier(12, -5, 2, -4, -7, -4, -16, -10);
                hp.CloseFigure();
                g.FillPath(bHead, hp);
            }

            using (var wp = new SD.Drawing2D.GraphicsPath())
            {
                wp.AddBezier(-16, -10, -28, 2, -26, 26, -9, 41);
                wp.AddBezier(-9, 41, -4, 46, 1, 46, 5, 41);
                wp.AddBezier(5, 41, 22, 26, 25, 4, 15, -5);
                wp.AddBezier(15, -5, 5, -8, -5, -8, -16, -10);
                wp.CloseFigure();
                g.FillPath(bHead, wp);
            }

            using (var tp = new SD.Drawing2D.GraphicsPath())
            {
                tp.AddBezier(-12, 33, -15, 47, -17, 57, -10, 60);
                tp.AddBezier(-10, 60, -4, 64, 3, 64, 9, 60);
                tp.AddBezier(9, 60, 15, 57, 14, 47, 10, 33);
                tp.CloseFigure();
                g.FillPath(bTail, tp);
            }
            g.Restore(st);
        }

        // ---------- diagnostics (Testes & Saúde) ----------

        void RecordNetworkAddressDelta() { RecordNetworkAddressDelta(false); }
    }
}
