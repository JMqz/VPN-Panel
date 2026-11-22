// Main window: start with Windows, network lock reset, full restore and legacy cleanup.

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
        void ToggleStartup()
        {
            startupTouched = true;
            if (!Paths.IsInstalled && chkStartup.IsChecked == true)
            {
                Dialogs.Info(w, "Instalação necessária", "Para iniciar automaticamente com o Windows de forma segura, instale o VPN Panel pelo instalador oficial.");
                loading = true;
                chkStartup.IsChecked = false;
                loading = false;
                return;
            }
            try
            {
                if (chkStartup.IsChecked == true) Tasks.Create(Tasks.Logon); else Tasks.Delete(Tasks.Logon);
            }
            catch (Exception ex)
            {
                Dialogs.Info(w, "Não foi possível alterar", "A inicialização com o Windows não foi alterada.\n\n" + ex.Message);
                loading = true;
                chkStartup.IsChecked = !(chkStartup.IsChecked == true);
                loading = false;
            }
        }

        bool HasActiveOrPendingTunnel
        {
            get { return wasRunning == true || connecting || reconnectPending || (reconnectTimer != null && reconnectTimer.IsEnabled); }
        }

        void DisconnectAndResetLock(Action onUiDone)
        {
            int seq = ++connectSeq;
            postDropCleanupSeq++;
            userStopped = true;
            connecting = false;
            connectPausedForDialog = false;
            connectLaunchTick = 0;
            wasRunning = false;
            runningConfig = null;
            reconnectPending = false;
            pendingPreConnectCleanup = false;
            reconnectAttempts = 0;
            if (reconnectTimer != null && reconnectTimer.IsEnabled) reconnectTimer.Stop();
            StopMonitoring();
            Traffic.Stop();
            ResetTrafficLabels();
            bool netLock = settings.NetworkLock;
            UpdateState();
            UpdatePollInterval();
            NetMon.Record("Bloqueio", NetMon.EventKind.Warn, "Desconectando túnel e resetando bloqueio de rede (WFP)");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tunnel.Stop(netLock);
                Tunnel.ResetNetworkLock();
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (exiting) return;
                        if (seq != connectSeq)
                        {
                            diagRepairRunning = false;
                            if (btnDiagQuick != null)
                            {
                                btnDiagQuick.IsEnabled = true;
                                btnDiagQuick.Content = "Testar agora";
                            }
                            if (btnDiagRepair != null)
                            {
                                btnDiagRepair.IsEnabled = true;
                                btnDiagRepair.Content = "Corrigir problemas encontrados";
                            }
                            return;
                        }
                        ipTimer.Stop();
                        ipTimer.Start();
                        Poll();
                        if (onUiDone != null) onUiDone();
                    }));
                }
            });
        }

        void VerifyPostResumeInternetAsync(bool netLockConfigured)
        {
            var resumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            resumeTimer.Tick += (s, e) =>
            {
                resumeTimer.Stop();
                if (exiting || connecting || Tunnel.Connecting || Tunnel.Active(true) ||
                    wasRunning == true || reconnectPending || reconnectTimer.IsEnabled ||
                    waitingForNetworkRestore || reconnectProbeRunning)
                    return;
                int seq = connectSeq;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (exiting || seq != connectSeq || Tunnel.Connecting || Tunnel.Active(true)) return;
                        AutoHealer.TryPostDropRecovery(netLockConfigured, true,
                            () => exiting || seq != connectSeq || Tunnel.Connecting || Tunnel.Active(true));
                    }
                    catch { }
                });
            };
            resumeTimer.Start();
        }

        void ResetNetworkLockAction()
        {
            bool stopExternalWs = false;
            if (Paths.WireSockServiceRunning)
            {
                if (!WindowShowing) ShowWindow();
                stopExternalWs = Dialogs.Confirm(w, "Encerrar serviço WireSock nativo?",
                    "O serviço nativo do WireSock (outro aplicativo WireSock) também está ativo. Deseja encerrá-lo junto com a restauração?",
                    "Encerrar também", true);
            }
            if (HasActiveOrPendingTunnel)
            {
                if (WindowShowing)
                {
                    bool ok = Dialogs.Confirm(w, "Restaurar internet padrão?",
                        "Para limpar bloqueios WFP, IPv6 fantasma e DNS preso com segurança, a VPN será desconectada antes.",
                        "Desconectar e restaurar", true);
                    if (!ok) return;
                }
                DisconnectAndResetLock(() => RunFullRestoreAfterStop(stopExternalWs));
                return;
            }
            RunFullRestoreAfterStop(stopExternalWs);
        }

        void RunFullRestoreAfterStop(bool stopExternalWs)
        {
            NetMon.Record("Bloqueio", NetMon.EventKind.Warn, "Restauração completa da internet padrão iniciada");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool hasUplink;
                NetDiag.ProbeResult udp, tcp;
                List<string> actions;
                AutoHealer.RestoreDefaultInternet(stopExternalWs, out hasUplink, out udp, out tcp, out actions);
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        ipTimer.Stop();
                        ipTimer.Start();
                        var sb = new StringBuilder();
                        if (actions != null && actions.Count > 0)
                        {
                            sb.AppendLine("Ações aplicadas:");
                            for (int i = 0; i < actions.Count; i++) sb.AppendLine("• " + actions[i]);
                            sb.AppendLine();
                        }
                        if (!hasUplink)
                        {
                            sb.Append("Sem link físico ativo no momento (verifique cabo/Wi-Fi). Bloqueios da VPN foram limpos.");
                        }
                        else if ((udp != null && udp.Ok) || (tcp != null && tcp.Ok))
                        {
                            string lat = (udp != null && udp.Ok ? "UDP " + udp.LatencyMs + " ms" : "UDP falhou") +
                                         " · " + (tcp != null && tcp.Ok ? "TCP " + tcp.LatencyMs + " ms" : "TCP falhou");
                            sb.Append("Internet base respondendo normalmente (" + lat + ").");
                        }
                        else
                        {
                            sb.Append("O VPN Panel limpou todos os bloqueios e rotas, mas 1.1.1.1 não respondeu. Verifique seu roteador ou outro serviço VPN.");
                        }
                        string msg = sb.ToString();
                        if (WindowShowing) Dialogs.Info(w, "Internet padrão restaurada", msg);
                        else if (tray != null) tray.ShowBalloonTip(3500, "Internet restaurada", msg.Replace("\n", " "), WF.ToolTipIcon.Info);
                    }));
                }
            });
        }

        void CleanLegacyAction()
        {
            if (cleanLegacyRunning) return;
            if (!Dialogs.Confirm(w, "Limpar versões antigas e resíduos?",
                "Isso remove arquivos temporários antigos, scripts legados (C:\\VPN), entradas/tarefas obsoletas de versões anteriores e comentários de DNS acidentais em servidores (com backup ZIP antes).\n\nSeus servidores e preferências atuais são preservados.",
                "Limpar agora", false))
                return;

            cleanLegacyRunning = true;
            if (btnCleanLegacy != null) btnCleanLegacy.IsEnabled = false;
            bool allowNet = !HasActiveOrPendingTunnel;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                LegacyCleanup.LegacyCleanReport rep = null;
                try
                {
                    rep = LegacyCleanup.Run(true, allowNet);
                }
                catch { }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        cleanLegacyRunning = false;
                        if (btnCleanLegacy != null) btnCleanLegacy.IsEnabled = true;
                        RefreshConfigs();
                        LoadAppsEditor();
                        if (rep == null || rep.CleanedCount == 0)
                        {
                            Dialogs.Info(w, "Tudo limpo", "Nenhum vestígio de versão antiga ou resíduo encontrado.");
                            return;
                        }
                        var sb = new StringBuilder();
                        sb.AppendLine("Itens limpos (" + rep.CleanedCount + "):");
                        for (int i = 0; i < rep.Items.Count; i++) sb.AppendLine("• " + rep.Items[i]);
                        if (!string.IsNullOrEmpty(rep.BackupZipPath))
                            sb.AppendLine("\nBackup ZIP salvo em:\n" + rep.BackupZipPath);
                        Dialogs.Info(w, "Limpeza concluída", sb.ToString());
                    }));
                }
                else
                {
                    cleanLegacyRunning = false;
                }
            });
        }

        // ---------- tray ----------

        static readonly WF.Padding TrayPadding = new WF.Padding(6, 4, 6, 4);
        const int TrayRecentCount = 3;
    }
}
