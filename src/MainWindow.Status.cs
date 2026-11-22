// Main window: balloons, banner, log tail, uptime and traffic labels.

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
        void Balloon(string text)
        {
            tray.ShowBalloonTip(3000, "VPN Panel", text, WF.ToolTipIcon.Warning);
        }

        // The banner on Início: red for errors, amber for warnings, with two actions
        void ShowBanner(bool warning, string title, string detail, string primary, Action onPrimary, string secondary, Action onSecondary)
        {
            errorBanner.Background = warning ? B("WarnSoft") : B("DangerSoft");
            errorBanner.BorderBrush = new SolidColorBrush(warning ? Color.FromArgb(0x66, 0xE3, 0xA5, 0x35) : Color.FromArgb(0x66, 0xF0, 0x56, 0x5B));
            E<TextBlock>("ErrIcon").Foreground = warning ? B("Warn") : B("Danger");
            txtErrTitle.Text = title;
            txtErrDetail.Text = detail;
            E<Button>("BtnRetry").Content = primary;
            E<Button>("BtnErrLog").Content = secondary;
            bannerPrimary = onPrimary;
            bannerSecondary = onSecondary;
            errorBanner.Visibility = Visibility.Visible;
            // make room: the IP card says little meanwhile
            E<Border>("IpCard").Visibility = Visibility.Collapsed;
            if (!w.IsVisible) Balloon(title + ". Clique para ver.");
        }

        void ShowError(string title, string detail)
        {
            ShowBanner(false, title, detail, "Tentar de novo", Connect, "Ver detalhes", OpenLog);
        }

        void HideBanner()
        {
            errorBanner.Visibility = Visibility.Collapsed;
            E<Border>("IpCard").Visibility = Visibility.Visible;
        }

        // Explains a failure in plain words, from the last error WireSock logged.
        // Log lines look like: 2026-10-25 15:25:16 {"log_level":"error","message":"[MGR]: ... [TUN]: Failed to ...: Invalid static private key"}
        static string LogTail()
        {
            string message = null;
            try
            {
                using (var fs = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > 32768) fs.Seek(fs.Length - 32768, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs))
                    {
                        string[] lines = r.ReadToEnd().Split('\n');
                        string warnFallback = null;
                        string plainFallback = null;
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            string l = lines[i].Trim();
                            if (l.Length == 0) continue;
                            string lvl;
                            string msg = ExtractLogMessage(l, out lvl);
                            if (string.IsNullOrEmpty(msg)) continue;
                            if (lvl == "error" || lvl == "fatal" || lvl == "critical") { message = msg; break; }
                            if (warnFallback == null && (lvl == "warning" || lvl == "warn")) warnFallback = msg;
                            if (plainFallback == null && string.IsNullOrEmpty(lvl))
                            {
                                string low = msg.ToLowerInvariant();
                                if (low.Contains("error") || low.Contains("exception") || low.Contains("failed") ||
                                    low.Contains("watchdog") || low.Contains("wintun") || low.Contains("ndis") ||
                                    low.Contains("access denied") || low.Contains("fatal"))
                                    plainFallback = msg;
                            }
                        }
                        if (string.IsNullOrEmpty(message)) message = warnFallback ?? plainFallback;
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(message))
                return "O WireSock fechou sem explicar o motivo. Tente de novo ou veja os detalhes.";

            string lower = message.ToLowerInvariant();
            if (lower.Contains("private key") || lower.Contains("public key") || lower.Contains("preshared"))
                return "O arquivo deste servidor tem uma chave inválida. Baixe o arquivo de novo no site do seu provedor de VPN.";
            if (lower.Contains("endpoint") || lower.Contains("resolve") || lower.Contains("host"))
                return "Não foi possível encontrar o endereço do servidor. Confira sua internet ou escolha outro servidor.";
            if (lower.Contains("address") || lower.Contains("allowedips") || lower.Contains("parse"))
                return "O arquivo deste servidor tem um erro de formato. Baixe o arquivo de novo no site do seu provedor.";
            if (lower.Contains("inject") || lower.Contains("deferred") || lower.Contains("bypass"))
                return "O driver do WireSock falhou ao repassar pacotes na placa de rede (conflito ou oscilação de adaptador).";
            if (lower.Contains("mtu"))
                return "Pacote excedeu o MTU configurado no túnel. Ative 'Otimizar MTU' nas Opções.";
            if (lower.Contains("watchdog") || lower.Contains("adapter list changed"))
                return "O WireSock reiniciou por alteração nos adaptadores de rede do Windows.";
            if (lower.Contains("driver") || lower.Contains("adapter") || lower.Contains("wintun") || lower.Contains("ndis"))
                return "O WireSock não conseguiu preparar a rede. Reinstale o WireSock e reinicie o computador.";
            if (lower.Contains("access") && lower.Contains("denied"))
                return "O Windows negou permissão ao WireSock. Feche e abra o VPN Panel de novo.";
            return message.Length > 140 ? message.Substring(0, 140) + "..." : message;
        }

        void UpdateUptimeText()
        {
            TimeSpan up = DateTime.Now - startedAt;
            txtUptime.Text = string.Format("Conectado há {0:00}:{1:00}:{2:00}", (int)up.TotalHours, up.Minutes, up.Seconds) + totalText;
        }

        // ---------- traffic ----------

        static readonly System.Globalization.CultureInfo PtBr = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

        bool TrafficShowing { get { return shown == Conn.On && WindowShowing && pageHome.Visibility == Visibility.Visible; } }

        // Only while Início is on screen: in the tray or on another tab the counters aren't even read
        void UpdateTraffic()
        {
            if (!TrafficShowing || !Traffic.Sample()) return;
            SetSpeed(txtDown, ref downKey, Traffic.InRate);
            SetSpeed(txtUp, ref upKey, Traffic.OutRate);
            ulong total = Traffic.SessionIn + Traffic.SessionOut;
            int key = SizeKey(total);
            if (key != totalKey)
            {
                totalKey = key;
                totalText = total < 1048576 ? "" : "  ·  " + FormatSize(total);
            }
        }

        void ResetTrafficLabels()
        {
            downKey = upKey = totalKey = -1;
            totalText = "";
            txtDown.Text = txtUp.Text = "0 KB/s";
        }

        // The text is rebuilt only when the rounded number changes (an idle connection allocates nothing)
        static void SetSpeed(TextBlock label, ref int shownKey, double bytesPerSec)
        {
            int key = SpeedKey(bytesPerSec);
            if (key == shownKey) return;
            shownKey = key;
            label.Text = key < 0 ? "—" : FormatSpeed(bytesPerSec);
        }

        static int SpeedKey(double bytesPerSec)
        {
            if (bytesPerSec < 0) return -1;
            double kb = bytesPerSec / 1024.0;
            if (kb < 1000) return (int)Math.Round(kb);
            double mb = kb / 1024.0;
            if (mb < 10) return 100000 + (int)Math.Round(mb * 10);
            if (mb < 1000) return 200000 + (int)Math.Round(mb);
            return 300000 + (int)Math.Round(mb / 10);
        }

        static string FormatSpeed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 1000) return string.Format(PtBr, "{0:0} KB/s", kb);
            double mb = kb / 1024.0;
            if (mb < 10) return string.Format(PtBr, "{0:0.0} MB/s", mb);
            if (mb < 1000) return string.Format(PtBr, "{0:0} MB/s", mb);
            return string.Format(PtBr, "{0:0.00} GB/s", mb / 1024.0);
        }

        static string FormatBits(double bytesPerSec)
        {
            double kbps = bytesPerSec * 8 / 1000;
            if (kbps < 1000) return string.Format(PtBr, "{0:0} kbps", kbps);
            return string.Format(PtBr, "{0:0.0} Mbps", kbps / 1000);
        }

        static int SizeKey(ulong bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb < 1) return 0;
            if (mb < 100) return 100000 + (int)Math.Round(mb * 10);
            if (mb < 1024) return 200000 + (int)Math.Round(mb);
            return 300000 + (int)Math.Round(mb / 1024 * 100);
        }

        static string FormatSize(ulong bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb < 1) return string.Format(PtBr, "{0:0} KB", bytes / 1024.0);
            if (mb < 100) return string.Format(PtBr, "{0:0.0} MB", mb);
            if (mb < 1024) return string.Format(PtBr, "{0:0} MB", mb);
            return string.Format(PtBr, "{0:0.00} GB", mb / 1024);
        }

        // Built when hovered, not every second
        string TrafficTip()
        {
            if (!Traffic.Running) return null;
            string tip = "Nesta conexão: " + FormatSize(Traffic.SessionIn) + " recebidos, " + FormatSize(Traffic.SessionOut) + " enviados.";
            if (Traffic.InRate >= 0 && Traffic.OutRate >= 0)
                tip += "\nEm bits: ↓ " + FormatBits(Traffic.InRate) + "  ·  ↑ " + FormatBits(Traffic.OutRate);
            if (active != null && active.Mode != AppMode.All)
                tip += "\nMedido na placa de rede: inclui os apps que não passam pela VPN.";
            return tip;
        }

        void UpdateSpinnerAnimation()
        {
            bool wantStep1 = installingWireSock && WindowShowing;
            if (wantStep1 != step1SpinnerRunning)
            {
                step1SpinnerRunning = wantStep1;
                if (wantStep1)
                    step1SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
                else step1SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }
    }
}
