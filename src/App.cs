// VPN Panel - tray app for the WireSock WireGuard client (wiresock-client.exe).
// This file: entry point (Program) and embedded resources (Res). Each other class has its own file;
// the UI lives in MainWindow*.cs / MainWindow.xaml / Theme.xaml. Build with build.ps1.

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

[assembly: AssemblyTitle("VPN Panel")]
[assembly: AssemblyDescription("Painel para WireSock WireGuard no Windows")]
[assembly: AssemblyCompany("VPN Panel")]
[assembly: AssemblyProduct("VPN Panel")]
[assembly: AssemblyCopyright("Copyright © 2026 VPN Panel")]
[assembly: AssemblyTrademark("VPN Panel")]
[assembly: ComVisible(false)]
[assembly: Guid("b7c93e48-8120-4a87-8d2b-65c71d798a21")]
[assembly: AssemblyVersion("3.3.0.0")]
[assembly: AssemblyFileVersion("3.3.0.0")]

namespace VpnPanel
{
    static class Program
    {
        // Same mutex as the old vpn-panel.ps1, so they never run together
        const string MutexName = @"Global\VpnPanelWireSock";
        public const string ShowEventName = @"Global\VpnPanelWireSockShow";
        public const string Version = "3.3.0";

        static int RunProbeCli(string[] args)
        {
            if (args == null || args.Length == 0) return -1;
            if (args[0].Equals("--probe-udp", StringComparison.OrdinalIgnoreCase))
            {
                string host = args.Length > 1 ? args[1] : "1.1.1.1";
                int port = 53;
                int timeoutMs = 1500;
                if (args.Length > 2) int.TryParse(args[2], out port);
                if (args.Length > 3) int.TryParse(args[3], out timeoutMs);
                NetDiag.ProbeResult res = NetDiag.ProbeUdpDns(host, port > 0 ? port : 53, Math.Max(200, timeoutMs), CancellationToken.None);
                if (res != null && res.Ok)
                {
                    Console.WriteLine("OK " + res.LatencyMs);
                    return 0;
                }
                Console.WriteLine("ERR " + (res != null && !string.IsNullOrEmpty(res.Detail) ? res.Detail : "Falha UDP"));
                return 1;
            }
            if (args[0].Equals("--probe-udp-loop", StringComparison.OrdinalIgnoreCase))
            {
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.Equals("QUIT", StringComparison.OrdinalIgnoreCase)) break;
                    string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string host = parts.Length > 0 ? parts[0] : "1.1.1.1";
                    int port = 53;
                    int timeoutMs = 400;
                    if (parts.Length > 1) int.TryParse(parts[1], out port);
                    if (parts.Length > 2) int.TryParse(parts[2], out timeoutMs);
                    NetDiag.ProbeResult res = NetDiag.ProbeUdpDns(host, port > 0 ? port : 53, Math.Max(200, timeoutMs), CancellationToken.None);
                    if (res != null && res.Ok) Console.WriteLine("OK " + res.LatencyMs);
                    else Console.WriteLine("ERR " + (res != null && !string.IsNullOrEmpty(res.Detail) ? res.Detail : "Falha UDP"));
                    Console.Out.Flush();
                }
                return 0;
            }
            return -1;
        }

        [STAThread]
        static void Main(string[] args)
        {
            int cliExit = RunProbeCli(args);
            if (cliExit >= 0)
            {
                Environment.ExitCode = cliExit;
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogCrash("AppDomain", e.ExceptionObject as Exception);
            };

            // The exe starts without admin rights (asInvoker) and hands off to an elevated copy:
            // through the "open" scheduled task when it exists (no UAC prompt), otherwise through UAC.
            // The task can't pass arguments, so --uninstall always goes through UAC.
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (args.Length == 0 && Paths.IsInstalled && Tasks.PointsHere(Tasks.Open) && Tasks.Run(Tasks.Open)) return;
                try { Process.Start(new ProcessStartInfo(Paths.Exe, string.Join(" ", args)) { UseShellExecute = true, Verb = "runas" }); }
                catch (Win32Exception) { } // the user declined the UAC prompt
                return;
            }
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Installer.RunUninstall();
                return;
            }
            if (args.Any(a => a.Equals("--apply-rollback", StringComparison.OrdinalIgnoreCase)))
            {
                RollbackManager.ApplyRollbackFromWorker(args);
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Already running: ask that instance to show its window
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); }
                    catch { }
                    return;
                }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                System.Windows.Forms.Application.EnableVisualStyles();
                Paths.Init();

                if (RollbackManager.CheckPreLaunchAutoRollback())
                    return;

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    LogCrash("Dispatcher", e.Exception);
                    e.Handled = true;
                };
                app.Resources = (ResourceDictionary)XamlReader.Parse(Res.Text("Theme.xaml"));
                new MainWindow(args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)));
                app.Run();
            }
        }

        public static void LogCrash(string source, Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Paths.Data);
                string crashFile = Path.Combine(Paths.Data, "crash.log");
                try
                {
                    var fi = new FileInfo(crashFile);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        byte[] all = File.ReadAllBytes(crashFile);
                        int keep = 128 * 1024;
                        int start = all.Length - keep;
                        using (var fs = new FileStream(crashFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                            fs.Write(all, start, keep);
                    }
                }
                catch { }
                var sb = new StringBuilder();
                sb.AppendFormat("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}: {3}\r\n{4}\r\n",
                    DateTime.Now, source, ex.GetType().FullName, ex.Message, ex.StackTrace);
                Exception inner = ex.InnerException;
                while (inner != null)
                {
                    sb.AppendFormat("Caused by: {0}: {1}\r\n{2}\r\n", inner.GetType().FullName, inner.Message, inner.StackTrace);
                    inner = inner.InnerException;
                }
                sb.AppendLine();
                File.AppendAllText(crashFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    static class Res
    {
        public static Stream Open(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("VpnPanel." + name);
        }

        public static string Text(string name)
        {
            using (var r = new StreamReader(Open(name))) return r.ReadToEnd();
        }
    }
}
