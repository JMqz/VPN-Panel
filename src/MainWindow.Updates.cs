// Main window: app update check, prompt and install.

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
        void CheckForUpdates(bool manual)
        {
            if (checkingForUpdates && !manual) return;
            checkingForUpdates = true;
            lastUpdateCheck = DateTime.UtcNow;
            if (manual)
            {
                txtUpdateBadge.Text = "...";
                btnCheckUpdate.IsEnabled = false;
            }
            Updater.Check((info, err) =>
            {
                checkingForUpdates = false;
                if (manual) btnCheckUpdate.IsEnabled = true;
                if (err != null)
                {
                    if (manual)
                    {
                        txtUpdateBadge.Text = "Erro";
                        Dialogs.Info(w, "Atualizações", "Não foi possível verificar no momento: " + err.Message);
                    }
                    return;
                }
                if (info != null && info.IsNewer)
                {
                    if (!manual && !string.IsNullOrEmpty(settings.SkippedUpdateVersion) &&
                        Updater.IsSameVersion(info.Version, settings.SkippedUpdateVersion))
                    {
                        txtUpdateBadge.Text = "Ignorada";
                        return;
                    }
                    if (string.IsNullOrEmpty(info.DownloadUrl))
                    {
                        // Asset still uploading to GitHub release: retry shortly
                        updateCheckTimer.Stop();
                        updateCheckTimer.Interval = TimeSpan.FromSeconds(8);
                        updateCheckTimer.Start();
                        if (manual) txtUpdateBadge.Text = "Preparando v" + info.Version + "...";
                        return;
                    }
                    availableUpdate = info;
                    txtUpdateBadge.Text = info.SameVersionUpdated ? "Nova build" : "v" + info.Version;
                    txtUpdateFoundTitle.Text = info.SameVersionUpdated
                        ? "Atualização disponível (v" + info.Version + ")"
                        : "Nova versão " + info.Version + " disponível";
                    txtUpdateFoundDetail.Text = "Clique em Atualizar agora para instalar.";
                    cardUpdate.Visibility = Visibility.Visible;
                    if (manual || WindowShowing)
                    {
                        pendingUpdatePrompt = false;
                        PromptUpdate(info, false);
                    }
                    else
                    {
                        pendingUpdatePrompt = true;
                        tray.ShowBalloonTip(3000, "VPN Panel", "Nova atualização (v" + info.Version + ") disponível! Clique para atualizar.", WF.ToolTipIcon.Info);
                    }
                }
                else
                {
                    txtUpdateBadge.Text = "Atualizado";
                    cardUpdate.Visibility = Visibility.Collapsed;
                    if (manual)
                    {
                        Dialogs.Info(w, "VPN Panel atualizado", "Você já está usando a versão mais recente (v" + Program.Version + ").");
                    }
                }
            });
        }

        void InstallUpdate()
        {
            if (availableUpdate == null) return;
            PromptUpdate(availableUpdate, true);
        }

        static string FormatReleaseNotes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lines = new List<string>();
            foreach (string rawLine in raw.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                line = Regex.Replace(line, @"^#+\s*", "");
                line = line.Replace("**", "");
                if (line.StartsWith("- ") || line.StartsWith("* "))
                    line = "\u2022 " + line.Substring(2).Trim();
                lines.Add(line);
            }
            return lines.Count > 0 ? string.Join("\n", lines.ToArray()) : null;
        }

        void PromptUpdate(Updater.ReleaseInfo info, bool startImmediately)
        {
            if (info == null || updateDialogOpen) return;
            updateDialogOpen = true;
            try
            {
                string title = info.SameVersionUpdated ? "Atualização disponível" : "Nova versão disponível";
                string msg = info.SameVersionUpdated
                    ? "Uma atualização com arquivos modificados da v" + info.Version + " foi encontrada.\n\nDeseja baixar e instalar a atualização agora?"
                    : "A versão v" + info.Version + " do VPN Panel está disponível (você está na v" + Program.Version + ").\n\nDeseja baixar e instalar a atualização agora?";
                Window d = Dialogs.Frame(w, title, 380);
                var body = new StackPanel();
                body.Children.Add(Dialogs.Message(msg));

                string notes = FormatReleaseNotes(info.Notes);
                if (!string.IsNullOrEmpty(notes))
                {
                    var notesCard = new Border
                    {
                        Background = Dialogs.B("Surface2"),
                        BorderBrush = Dialogs.B("Line"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = new Thickness(0, 12, 0, 0)
                    };
                    var notesStack = new StackPanel();
                    notesStack.Children.Add(new TextBlock
                    {
                        Text = "O QUE MUDOU",
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Dialogs.B("Accent"),
                        Margin = new Thickness(0, 0, 0, 6)
                    });
                    var scroll = new ScrollViewer
                    {
                        MaxHeight = 140,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    };
                    scroll.Content = new TextBlock
                    {
                        Text = notes,
                        Foreground = Dialogs.B("Text"),
                        FontSize = 12.5,
                        LineHeight = 18,
                        TextWrapping = TextWrapping.Wrap
                    };
                    notesStack.Children.Add(scroll);
                    notesCard.Child = notesStack;
                    body.Children.Add(notesCard);
                }
                var status = new TextBlock
                {
                    Foreground = Dialogs.B("Accent"),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Visibility = Visibility.Collapsed
                };
                body.Children.Add(status);

                Button btnLater = Dialogs.Btn("Depois", "GhostButton");
                Button btnNow = Dialogs.Btn("Atualizar agora", "PrimaryButton");
                btnLater.IsCancel = true;
                btnNow.IsDefault = true;

                WebClient activeWc = null;
                bool downloading = false;

                Action startDownload = () =>
                {
                    if (downloading) return;
                    if (string.IsNullOrEmpty(info.DownloadUrl) ||
                        !info.DownloadUrl.StartsWith("https://github.com/JMqz/VPN-Panel/releases/download/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(info.HtmlUrl))
                            Process.Start(info.HtmlUrl);
                        d.Close();
                        return;
                    }

                    downloading = true;
                    btnNow.IsEnabled = false;
                    btnLater.IsEnabled = false;
                    btnNow.Content = "Baixando...";
                    status.Foreground = Dialogs.B("Accent");
                    status.Text = "Baixando a versão v" + info.Version + "...";
                    status.Visibility = Visibility.Visible;

                    btnApplyUpdate.IsEnabled = false;
                    btnApplyUpdate.Content = "Baixando...";
                    txtUpdateFoundDetail.Text = "Baixando a versão " + info.Version + "...";

                    string updateDir = System.IO.Path.Combine(Paths.Data, "updates");
                    try { Directory.CreateDirectory(updateDir); } catch { }
                    try
                    {
                        foreach (string old in Directory.GetFiles(updateDir, "*.exe"))
                        {
                            try { File.Delete(old); } catch { }
                        }
                    }
                    catch { }
                    string tempFile = System.IO.Path.Combine(updateDir, "VPN-Panel-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    var wc = new WebClient();
                    activeWc = wc;
                    wc.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                    wc.Headers["User-Agent"] = "vpn-panel";
                    wc.Headers["Cache-Control"] = "no-cache";
                    int lastPct = -1;
                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        if (e.ProgressPercentage == lastPct) return;
                        lastPct = e.ProgressPercentage;
                        string pctText = "Baixando (" + e.ProgressPercentage + "%)...";
                        btnNow.Content = pctText;
                        btnApplyUpdate.Content = pctText;
                        status.Text = "Baixando o instalador (" + e.ProgressPercentage + "%)...";
                    };
                    wc.DownloadFileCompleted += (s, e) =>
                    {
                        wc.Dispose();
                        activeWc = null;
                        downloading = false;
                        if (e.Cancelled)
                        {
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                            return;
                        }
                        if (e.Error != null)
                        {
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                            btnNow.IsEnabled = true;
                            btnLater.IsEnabled = true;
                            btnNow.Content = "Tentar de novo";
                            status.Foreground = Dialogs.B("Danger");
                            status.Text = "Erro ao baixar: " + e.Error.Message;
                            btnApplyUpdate.IsEnabled = true;
                            btnApplyUpdate.Content = "Atualizar agora";
                            txtUpdateFoundDetail.Text = "Erro ao baixar: " + e.Error.Message;
                            return;
                        }

                        try
                        {
                            var fi = new FileInfo(tempFile);
                            if (!fi.Exists || fi.Length < 100 * 1024)
                            {
                                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                                throw new InvalidOperationException("O instalador baixado é inválido ou está incompleto.");
                            }

                            // The installer runs with admin rights from a folder any program of this user can
                            // write to, so it is held open (no one can swap it) from the check until it starts.
                            string verifyError = null;
                            using (var lockStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                verifyError = Updater.VerifyDownload(tempFile, info.SetupDigest);
                                if (verifyError == null)
                                {
                                    status.Foreground = Dialogs.B("Accent");
                                    status.Text = "Instalando atualização...";
                                    btnNow.Content = "Instalando...";
                                    txtUpdateFoundDetail.Text = "Instalando atualização...";
                                    btnApplyUpdate.Content = "Instalando...";

                                    settings.InstalledDigest = info.SetupDigest;
                                    settings.SkippedUpdateVersion = "";
                                    settings.Save();

                                    bool installed = Paths.ExeDir.Equals(Installer.Dir, StringComparison.OrdinalIgnoreCase);
                                    var psi = new ProcessStartInfo(tempFile, installed ? "--silent" : "") { UseShellExecute = true };
                                    Process.Start(psi);
                                }
                            }
                            if (verifyError != null)
                            {
                                try { File.Delete(tempFile); } catch { }
                                throw new InvalidOperationException(verifyError);
                            }
                            d.Close();
                            Quit();
                        }
                        catch (Exception ex)
                        {
                            btnNow.IsEnabled = true;
                            btnLater.IsEnabled = true;
                            btnNow.Content = "Tentar de novo";
                            status.Foreground = Dialogs.B("Danger");
                            status.Text = "Erro ao instalar: " + ex.Message;
                            btnApplyUpdate.IsEnabled = true;
                            btnApplyUpdate.Content = "Atualizar agora";
                            txtUpdateFoundDetail.Text = "Erro ao instalar: " + ex.Message;
                        }
                    };

                    wc.DownloadFileAsync(new Uri(info.DownloadUrl), tempFile);
                };

                btnNow.Click += (s, e) => startDownload();
                btnLater.Click += (s, e) => { if (!downloading) d.Close(); };
                d.Closing += (s, e) =>
                {
                    if (activeWc != null)
                    {
                        try { activeWc.CancelAsync(); } catch { }
                    }
                };
                if (startImmediately)
                {
                    d.Loaded += (s, e) => startDownload();
                }

                Dialogs.Layout(d, title, body, btnLater, btnNow);
                d.ShowDialog();
            }
            finally
            {
                updateDialogOpen = false;
            }
        }

        // Ready = WireSock installed and at least one server; until then Início shows the setup steps
        bool Ready { get { return wireSockInstalled && active != null; } }

        FrameworkElement HomePage { get { return Ready ? pageHome : pageSetup; } }
    }
}
