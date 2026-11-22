// Main window: page switching, setup steps and the Options tab (kill switch, MTU, engine, settings).

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
        void ShowPage(FrameworkElement page)
        {
            foreach (FrameworkElement p in new[] { pageSetup, pageHome, pageServers, pageApps, pageDiag, pageOptions })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            if (page == pageServers && serversDirty) RenderServerList();
            // pings nobody will see: drop what hasn't started (going back to Servidores queues them again)
            if (page != pageServers) Latency.CancelQueued();
            if (page == pageHome) { UpdateTraffic(); if (shown == Conn.On) UpdateUptimeText(); }
            if (page == pageDiag) OnDiagPageShown();
        }

        void UpdateSetup()
        {
            SetStep(step1Badge, step1Mark, "1", wireSockInstalled);
            step1Done.Visibility = wireSockInstalled ? Visibility.Visible : Visibility.Collapsed;
            step1Actions.Visibility = wireSockInstalled ? Visibility.Collapsed : Visibility.Visible;
            SetStep(step2Badge, step2Mark, "2", active != null);
            // a server downloaded before the app was open
            foundDownload = active == null ? RecentDownload() : null;
            E<FrameworkElement>("Step2Found").Visibility = foundDownload != null ? Visibility.Visible : Visibility.Collapsed;
            if (foundDownload != null)
                E<TextBlock>("TxtStep2Found").Text = "Achamos \u201c" + System.IO.Path.GetFileName(foundDownload) + "\u201d na pasta Downloads.";
            if (tabHome.IsChecked == true && HomePage.Visibility != Visibility.Visible) ShowPage(HomePage);
        }

        static void SetStep(Border badge, TextBlock mark, string number, bool done)
        {
            badge.Background = done ? B("Accent") : B("Surface2");
            mark.Text = done ? "" : number;
            mark.FontFamily = done ? (FontFamily)Application.Current.FindResource("Icons") : (FontFamily)Application.Current.FindResource("Font");
            mark.Foreground = done ? B("OnAccent") : B("Text");
        }

        AppMode EffectiveAppMode()
        {
            if (settings.SharedApps)
                return settings.AppsMode != AppMode.All && settings.Apps.Count == 0 ? AppMode.All : settings.AppsMode;
            return active != null ? active.Mode : AppMode.All;
        }

        void UpdateLockAvailability()
        {
            if (chkLock == null) return;
            bool split = EffectiveAppMode() != AppMode.All;
            loading = true;
            chkLock.IsChecked = !split && settings.NetworkLock;
            loading = false;
            chkLock.IsEnabled = !split;
            if (txtLockNote != null)
            {
                if (split)
                {
                    txtLockNote.Text = "Indisponível no modo Só estes / Exceto (o bloqueio impediria o DNS e apps fora da VPN).";
                    txtLockNote.Visibility = Visibility.Visible;
                }
                else
                {
                    txtLockNote.Visibility = Visibility.Collapsed;
                }
            }
            UpdateDiagRouteBadge();
        }

        int EffectiveMaxMtu()
        {
            return settings.OptimizeMtu ? (settings.AdaptiveMtu > 0 ? settings.AdaptiveMtu : 1380) : 0;
        }

        static int DiagEffectiveMtu(bool tunnelRunning, AppMode effMode, Settings s)
        {
            if (s == null || !s.OptimizeMtu) return 0;
            return (tunnelRunning && effMode == AppMode.All)
                ? (s.AdaptiveMtu > 0 ? s.AdaptiveMtu : 1380)
                : s.AdaptiveMtu;
        }

        void SyncMtuUiFromSettings()
        {
            if (chkMtu == null) return;
            bool wasLoading = loading;
            loading = true;
            chkMtu.IsChecked = settings.OptimizeMtu;
            loading = wasLoading;
        }

        void ApplyHealReportToSettings(AutoHealer.FixReport report)
        {
            if (report == null) return;
            bool changed = false;
            if (report.EnableOptimizeMtu && !settings.OptimizeMtu)
            {
                settings.OptimizeMtu = true;
                changed = true;
            }
            if (report.SuggestedMtu > 0 && (settings.AdaptiveMtu == 0 || report.SuggestedMtu < settings.AdaptiveMtu))
            {
                settings.AdaptiveMtu = report.SuggestedMtu;
                changed = true;
            }
            if (changed)
            {
                SyncMtuUiFromSettings();
                settings.Save();
            }
        }

        void UpdateEngineUi()
        {
            if (txtEngineStatus == null || btnInstallWireGuard == null) return;
            if (installingWireGuard)
            {
                btnInstallWireGuard.IsEnabled = false;
                btnInstallWireGuard.Visibility = Visibility.Visible;
                txtEngineStatus.Text = "Instalando WireGuard (Wintun)... aguarde um momento.";
                return;
            }
            bool hasWg = Paths.WireGuard != null;
            btnInstallWireGuard.IsEnabled = true;
            btnInstallWireGuard.Visibility = hasWg ? Visibility.Collapsed : Visibility.Visible;
            if (hasWg)
            {
                txtEngineStatus.Text = settings.EnginePreference == VpnEnginePreference.WireSock
                    ? "WireGuard (Wintun) instalado, mas forçado para WireSock abaixo."
                    : "WireGuard (Wintun L3) pronto — modo 'Todos' roda sem filtro NDIS na placa física.";
            }
            else
            {
                txtEngineStatus.Text = "Wintun opcional não instalado (usando WireSock). Instale para eliminar filtro NDIS no modo 'Todos'.";
            }
        }

        void InstallWireGuard()
        {
            if (installingWireGuard) return;
            installingWireGuard = true;
            UpdateEngineUi();
            new Thread(() =>
            {
                string err = WireGuardSetup.Install(msg =>
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null) ui.BeginInvoke(new Action(() =>
                    {
                        if (!exiting && txtEngineStatus != null) txtEngineStatus.Text = msg;
                    }));
                });
                var uiDone = Application.Current != null ? Application.Current.Dispatcher : null;
                if (uiDone != null)
                {
                    uiDone.BeginInvoke(new Action(() =>
                    {
                        installingWireGuard = false;
                        if (exiting) return;
                        UpdateEngineUi();
                        if (err == null)
                        {
                            NetMon.Record("Sistema", NetMon.EventKind.Info, "WireGuard (Wintun) instalado — modo Todos pronto para Wintun L3");
                            if (wasRunning == true && EffectiveAppMode() == AppMode.All && settings.EnginePreference == VpnEnginePreference.Auto)
                                NotifyOptionNeedsReconnect();
                            else
                                Dialogs.Info(w, "Wintun instalado", "WireGuard (Wintun) instalado com sucesso. No modo 'Todos os apps', a VPN agora usa o driver L3 isolado.");
                        }
                        else
                        {
                            Dialogs.Info(w, "Instalação do Wintun", err);
                        }
                    }));
                }
            }) { IsBackground = true }.Start();
        }

        void NotifyOptionNeedsReconnect()
        {
            if (exiting || wasRunning != true) return;
            ShowBanner(true, "Ajuste salvo para a próxima conexão",
                "Reconecte a VPN para aplicar a mudança de Motor / Modo Jogos / DNS no túnel atual.",
                "Reconectar agora", Reconnect, "Depois", HideBanner);
        }

        void SaveSettings()
        {
            settings.Config = active != null ? active.Name : null;
            settings.AutoReconnect = chkReconnect.IsChecked == true;
            if (chkLock.IsEnabled) settings.NetworkLock = chkLock.IsChecked == true;
            settings.OptimizeMtu = chkMtu.IsChecked == true;
            if (!settings.OptimizeMtu) settings.AdaptiveMtu = 0;
            settings.GameStabilityMode = chkGameMode != null ? chkGameMode.IsChecked == true : settings.GameStabilityMode;
            if (segDnsCloudflare != null && segDnsCloudflare.IsChecked == true) settings.CustomDns = "1.1.1.1";
            else if (segDnsQuad9 != null && segDnsQuad9.IsChecked == true) settings.CustomDns = "9.9.9.9";
            else if (segDnsDefault != null && segDnsDefault.IsChecked == true) settings.CustomDns = "";
            if (segEngineWireSock != null && segEngineWireSock.IsChecked == true) settings.EnginePreference = VpnEnginePreference.WireSock;
            else if (segEngineAuto != null && segEngineAuto.IsChecked == true) settings.EnginePreference = VpnEnginePreference.Auto;
            settings.AutoHeal = chkAutoHeal != null ? chkAutoHeal.IsChecked == true : settings.AutoHeal;
            settings.AutoConnect = chkAuto.IsChecked == true;
            settings.SkipUac = chkSkipUac.IsChecked == true;
            settings.Hotkey = chkHotkey.IsChecked == true;
            settings.CheckUpdates = chkUpdates.IsChecked == true;
            settings.Save();
        }

        static Brush B(string key) { return Dialogs.B(key); }

        // "Discord", "Discord e Chrome", "Discord, Chrome e Steam"
        static string JoinPt(IList<string> items)
        {
            if (items.Count <= 1) return string.Join("", items);
            return string.Join(", ", items.Take(items.Count - 1)) + " e " + items[items.Count - 1];
        }

        // ---------- connection ----------
    }
}
