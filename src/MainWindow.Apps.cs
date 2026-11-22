// Main window: Apps tab: split tunneling editor and app icons.

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
        static bool SameApps(ConfigInfo c, AppMode mode, List<string> apps)
        {
            if (c == null || !c.Readable) return false;
            AppMode effMode = mode != AppMode.All && apps.Count == 0 ? AppMode.All : mode;
            if (c.Mode != effMode) return false;
            if (effMode == AppMode.All) return true;
            if (c.Apps.Count != apps.Count) return false;
            for (int i = 0; i < apps.Count; i++)
                if (!c.Apps[i].Equals(apps[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // First run with this setting: if existing servers already have different app lists, keep per-server mode so nothing is lost
        void MigrateSharedApps()
        {
            if (settings.HasSharedAppsKey) return;
            string[] names = Paths.ConfigNames();
            if (names.Length == 0) return;
            settings.HasSharedAppsKey = true;
            ConfigInfo first = null;
            bool same = true;
            foreach (string n in names)
            {
                ConfigInfo c = active != null && n == active.Name ? active : ConfigInfo.Read(n);
                if (!c.Readable) continue;
                if (first == null) first = c;
                else if (!SameApps(c, first.Mode, first.Apps)) { same = false; break; }
            }
            settings.SharedApps = same;
            if (same && active != null && active.Readable)
            {
                settings.AppsMode = active.Mode;
                settings.Apps = new List<string>(active.Apps);
            }
            settings.Save();
        }

        void ToggleSharedApps()
        {
            settings.SharedApps = chkSharedApps.IsChecked == true;
            if (settings.SharedApps)
            {
                settings.AppsMode = editMode;
                settings.Apps = editMode == AppMode.All ? new List<string>() : new List<string>(editApps);
                settings.Save();
                if (active != null && IsLive(active.Name))
                    appsNeedReconnect = !SameApps(active, settings.AppsMode, settings.Apps);
            }
            else
            {
                settings.Save();
                if (active != null && (editMode == AppMode.All || editApps.Count > 0))
                {
                    try { ConfFile.WriteApps(Paths.Conf(active.Name), editMode, editMode == AppMode.All ? new List<string>() : editApps); }
                    catch { }
                    if (IsLive(active.Name)) appsNeedReconnect = !SameApps(active, editMode, editApps);
                }
            }
            RefreshConfigs();
            LoadAppsEditor();
        }

        void LoadAppsEditor()
        {
            if (settings.SharedApps)
            {
                editApps = new List<string>(settings.Apps);
                editMode = settings.AppsMode;
                txtAppsCfg.Text = active != null ? "Vale para todos os servidores" : "Adicione um servidor primeiro";
            }
            else
            {
                editApps = active != null ? new List<string>(active.Apps) : new List<string>();
                editMode = active != null ? active.Mode : AppMode.All;
                txtAppsCfg.Text = active != null ? "Só para o servidor: " + active.Name : "Adicione um servidor primeiro";
            }
            loading = true;
            chkSharedApps.IsChecked = settings.SharedApps;
            (editMode == AppMode.Only ? segOnly : editMode == AppMode.Except ? segExcept : segAll).IsChecked = true;
            loading = false;
            pageApps.IsEnabled = active != null;
            RenderApps();
        }

        void SetMode(AppMode mode)
        {
            editMode = mode;
            RenderApps();
            if (!loading) PersistApps();
        }

        void RenderApps()
        {
            txtModeHint.Text =
                editMode == AppMode.Only ? "Só os apps da lista passam pela VPN. O resto usa a sua internet normal." :
                editMode == AppMode.Except ? "Tudo passa pela VPN, menos os apps da lista." :
                "Todo o tráfego do computador passa pela VPN. É o mais seguro.";
            bool list = editMode != AppMode.All;
            appsAdd.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            appListCard.Visibility = list ? Visibility.Visible : Visibility.Collapsed;

            // the list (icons look up running processes) is only built while someone can see it
            appsDirty = !WindowShowing;
            if (appsDirty) { UpdateAppsSaved(); return; }
            appList.Children.Clear();
            if (editApps.Count == 0)
                appList.Children.Add(new TextBlock
                {
                    Text = "Nenhum app ainda. Escolha um dos apps abertos ou procure o programa.",
                    Style = Dialogs.S("Caption"), TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 18, 16, 18),
                });
            foreach (string app in editApps)
            {
                string name = app;
                var row = new Grid { Margin = new Thickness(6, 2, 2, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Dialogs.AppAvatar(name, AppIcon(name, null)));
                var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                var remove = new Button { Style = Dialogs.S("IconDangerButton"), Content = "", FontSize = 11, ToolTip = "Remover" };
                AutomationProperties.SetName(remove, "Remover " + name);
                remove.Click += (s, e) => { editApps.Remove(name); RenderApps(); PersistApps(); };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                appList.Children.Add(row);
            }
            UpdateAppsSaved();
        }

        // Saves the editor right away (unless the chosen mode still has an empty list)
        void PersistApps()
        {
            if (active == null) return;
            if (editMode != AppMode.All && editApps.Count == 0) { UpdateAppsSaved(); return; }
            if (settings.SharedApps)
            {
                settings.AppsMode = editMode;
                settings.Apps = editMode == AppMode.All ? new List<string>() : new List<string>(editApps);
                settings.Save();
                if (IsLive(active.Name)) appsNeedReconnect = !SameApps(active, settings.AppsMode, settings.Apps);
                UpdateAppsSaved();
                UpdateLockAvailability();
                return;
            }
            try { ConfFile.WriteApps(Paths.Conf(active.Name), editMode, editMode == AppMode.All ? new List<string>() : editApps); }
            catch (Exception ex) { Dialogs.Info(w, "Não foi possível salvar", ex.Message); return; }
            if (IsLive(active.Name)) appsNeedReconnect = true;
            RefreshConfigs();
        }

        void UpdateAppsSaved()
        {
            RenderHomeApps();
            if (applyBanner == null || active == null) return;
            bool incomplete = editMode != AppMode.All && editApps.Count == 0;
            bool showApply = appsNeedReconnect && IsLive(active.Name) && !incomplete;
            applyBanner.Visibility = showApply ? Visibility.Visible : Visibility.Collapsed;
            txtAppsSaved.Visibility = showApply ? Visibility.Collapsed : Visibility.Visible;
            txtAppsSaved.Text = incomplete ? "Adicione pelo menos um app para usar este modo." : "As mudanças são salvas automaticamente.";
            txtAppsSaved.Foreground = incomplete ? B("Warn") : B("Muted");
        }

        // One click for the most common use: only Discord (voice, video and screen sharing) through the VPN
        void ApplyDiscordPreset()
        {
            if (active == null) return;
            bool onlyDiscord = editMode == AppMode.Only && editApps.Count == 1 &&
                               editApps[0].Equals("Discord", StringComparison.OrdinalIgnoreCase);
            if (!onlyDiscord)
            {
                if (editMode == AppMode.Only)
                {
                    if (!editApps.Any(a => a.Equals("Discord", StringComparison.OrdinalIgnoreCase))) editApps.Add("Discord");
                }
                else
                {
                    if (editMode == AppMode.Except && editApps.Count > 0 &&
                        !Dialogs.Confirm(w, "Trocar para Só o Discord?",
                            "A lista atual do modo \"Todos, exceto\" será substituída: só o Discord vai passar pela VPN.",
                            "Trocar", false))
                        return;
                    editApps = new List<string> { "Discord" };
                    editMode = AppMode.Only;
                    loading = true;
                    segOnly.IsChecked = true;
                    loading = false;
                }
                RenderApps();
                PersistApps();
            }
            Dialogs.Info(w, "Só o Discord na VPN",
                "Voz, vídeo e transmissão de tela do Discord passam pela VPN; jogos e o resto usam a sua internet normal." +
                (wasRunning == true ? " Reconecte para aplicar." : "") +
                "\n\nDica: se a voz ou a transmissão falharem, desligue no Discord a opção \"Qualidade de serviço: prioridade alta de pacotes\" (Configurações → Voz e vídeo).");
        }

        void AddApps(IEnumerable<string> names)
        {
            bool changed = false;
            foreach (string raw in names)
            {
                string name = raw.Trim().Trim('"').Trim().Replace(",", "");
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (name.Length == 0 || editApps.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                editApps.Add(name);
                changed = true;
            }
            RenderApps();
            if (changed) PersistApps();
        }

        void BrowseExe()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o programa", Filter = "Programas (*.exe)|*.exe", Multiselect = true };
            if (ofd.ShowDialog(w) != true) return;
            foreach (string f in ofd.FileNames) AppIcon(System.IO.Path.GetFileNameWithoutExtension(f), f);
            AddApps(ofd.FileNames.Select(f => System.IO.Path.GetFileNameWithoutExtension(f)));
        }

        static readonly Dictionary<string, string> appPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string FindAppExeOrLnk(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string cached;
            if (appPathCache.TryGetValue(name, out cached))
            {
                if (cached != null && File.Exists(cached)) return cached;
                if (cached == null) return null;
            }

            string exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            string nameOnly = System.IO.Path.GetFileNameWithoutExtension(name);

            // 1. Check running processes
            try
            {
                foreach (Process p in Process.GetProcessesByName(nameOnly))
                {
                    try
                    {
                        string pPath = ProcUtil.ExePath(p.Id);
                        if (!string.IsNullOrEmpty(pPath) && File.Exists(pPath))
                        {
                            appPathCache[name] = pPath;
                            return pPath;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }

            // 2. Registry App Paths (HKCU & HKLM, 64-bit and 32-bit views)
            try
            {
                string[] regTargets = { exeName, nameOnly };
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                    {
                        try
                        {
                            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                            {
                                foreach (var target in regTargets)
                                {
                                    using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + target))
                                    {
                                        if (key != null)
                                        {
                                            object val = key.GetValue(null);
                                            if (val != null)
                                            {
                                                string p = val.ToString().Trim('\"');
                                                if (File.Exists(p))
                                                {
                                                    appPathCache[name] = p;
                                                    return p;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 3. Known folder locations
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                string[] directChecks = {
                    System.IO.Path.Combine(localApp, nameOnly, exeName),
                    System.IO.Path.Combine(localApp, "Programs", nameOnly, exeName),
                    System.IO.Path.Combine(progFiles, nameOnly, exeName),
                    System.IO.Path.Combine(progFilesX86, nameOnly, exeName),
                    System.IO.Path.Combine(roaming, nameOnly, exeName),
                    System.IO.Path.Combine(progFiles, "Google", "Chrome", "Application", "chrome.exe"),
                    System.IO.Path.Combine(progFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                    System.IO.Path.Combine(progFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    System.IO.Path.Combine(progFiles, "Mozilla Firefox", "firefox.exe"),
                    System.IO.Path.Combine(progFilesX86, "Steam", "steam.exe")
                };
                foreach (string p in directChecks)
                {
                    if (File.Exists(p))
                    {
                        if (System.IO.Path.GetFileNameWithoutExtension(p).Equals(nameOnly, StringComparison.OrdinalIgnoreCase))
                        {
                            appPathCache[name] = p;
                            return p;
                        }
                    }
                }

                // Squirrel / Electron app-* directories (Discord, Slack, Teams, etc.)
                string appDir = System.IO.Path.Combine(localApp, nameOnly);
                if (Directory.Exists(appDir))
                {
                    foreach (string sub in Directory.GetDirectories(appDir, "app-*"))
                    {
                        string candidate = System.IO.Path.Combine(sub, exeName);
                        if (File.Exists(candidate))
                        {
                            appPathCache[name] = candidate;
                            return candidate;
                        }
                    }
                }
            }
            catch { }

            // 4. Start Menu shortcuts (.lnk files)
            try
            {
                string[] startMenuDirs = {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                };
                foreach (string smDir in startMenuDirs)
                {
                    if (Directory.Exists(smDir))
                    {
                        string[] lnks = Directory.GetFiles(smDir, nameOnly + "*.lnk", SearchOption.AllDirectories);
                        if (lnks.Length > 0 && File.Exists(lnks[0]))
                        {
                            appPathCache[name] = lnks[0];
                            return lnks[0];
                        }
                    }
                }
            }
            catch { }

            appPathCache[name] = null;
            return null;
        }

        // Icon of a program, from the given path or from a running process or on-disk executable
        ImageSource AppIcon(string name, string path)
        {
            ImageSource cached;
            if (iconCache.TryGetValue(name, out cached) && cached != null) return cached;
            if (path == null)
            {
                DateTime miss;
                if (iconMisses.TryGetValue(name, out miss) && (DateTime.UtcNow - miss).TotalSeconds < 30) return null;
            }
            ImageSource img = null;
            try
            {
                if (path == null)
                {
                    path = FindAppExeOrLnk(name);
                }
                if (path != null && File.Exists(path))
                {
                    using (SD.Icon icon = SD.Icon.ExtractAssociatedIcon(path))
                    {
                        if (icon != null)
                        {
                            img = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            img.Freeze();
                        }
                    }
                }
            }
            catch { }
            if (img != null)
            {
                iconCache[name] = img;
                iconMisses.Remove(name);
            }
            else iconMisses[name] = DateTime.UtcNow;
            return img;
        }

        // ---------- options ----------
    }
}
