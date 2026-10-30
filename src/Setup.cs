// Installer ("VPN Panel Setup.exe") and uninstaller ("VpnPanel.exe --uninstall").
// Installs to Program Files (only admins can change the exe that the no-UAC task starts),
// adds Start menu / desktop shortcuts and the entry in Windows' installed apps list.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace VpnPanel
{
    static class Installer
    {
        public const string AppName = "VPN Panel";
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        public static readonly string Exe = Path.Combine(Dir, "VpnPanel.exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");
        static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VPN Panel";

        public static string InstalledVersion
        {
            get
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                    return k != null && File.Exists(Exe) ? k.GetValue("DisplayVersion") as string : null;
            }
        }

        // Closes other running copies of the app (the VPN tunnel itself keeps running)
        static void StopApp()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("VpnPanel"))
            {
                try
                {
                    if (p.Id != self) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                p.Dispose();
            }
        }

        static void Shortcut(string link)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic s = shell.CreateShortcut(link);
            s.TargetPath = Exe;
            s.WorkingDirectory = Dir;
            s.IconLocation = Exe + ",0";
            s.Description = "Conecte sua VPN WireGuard com um clique";
            s.Save();
        }

        // Keeps the user's choice when updating without the window
        public static bool HasDesktopShortcut
        {
            get
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    string link = k != null ? k.GetValue("DesktopShortcut") as string : null;
                    if (!string.IsNullOrEmpty(link) && File.Exists(link)) return true;
                }
                return File.Exists(DesktopLink);
            }
        }

        public static void Install(byte[] exe, bool desktopShortcut)
        {
            StopApp();
            Directory.CreateDirectory(Dir);
            // the old exe can stay locked for a moment after its process ends
            for (int i = 0; ; i++)
            {
                try { File.WriteAllBytes(Exe, exe); break; }
                catch (IOException) { if (i >= 10) throw; Thread.Sleep(300); }
            }

            Shortcut(StartMenuLink);
            if (desktopShortcut) Shortcut(DesktopLink);
            else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", AppName);
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, exe.Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("DesktopShortcut", desktopShortcut ? DesktopLink : "");
            }
        }

        public static void Uninstall(bool removeData)
        {
            StopApp();
            Tunnel.Stop(true); // never leave the kill switch blocking a machine without the panel
            try { Tasks.Delete(Tasks.Logon); } catch { }
            try { Tasks.Delete(Tasks.Open); } catch { }

            string desktop = null;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                if (k != null) desktop = k.GetValue("DesktopShortcut") as string;
            foreach (string link in new[] { StartMenuLink, DesktopLink, desktop })
                if (!string.IsNullOrEmpty(link) && File.Exists(link)) try { File.Delete(link); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey); } catch { }

            if (removeData && Directory.Exists(Paths.Data)) try { Directory.Delete(Paths.Data, true); } catch { }
        }

        // The running exe can't delete its own folder: a hidden cmd keeps trying for ~30 s after we exit
        static void DeleteFolderAfterExit()
        {
            if (!Paths.ExeDir.Equals(Dir, StringComparison.OrdinalIgnoreCase)) return;
            string q = "\"" + Dir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c for /l %i in (1,1,15) do (ping -n 3 127.0.0.1 >nul & rmdir /s /q " + q + " 2>nul & if not exist " + q + " exit)")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            }).Dispose();
        }

        public static Application ThemedApp()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources = (ResourceDictionary)XamlReader.Parse(Res.Text("Theme.xaml"));
            return app;
        }

        // VpnPanel.exe --uninstall (from Windows' installed apps list)
        public static void RunUninstall()
        {
            ThemedApp();
            Window d = Dialogs.Frame(null, "Desinstalar VPN Panel", 380);
            var body = new StackPanel();
            body.Children.Add(Dialogs.Message("O VPN Panel será removido deste computador e a VPN será desconectada."));
            var keep = new CheckBox { Style = Dialogs.S("Switch"), Margin = new Thickness(0, 16, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(keep, "Apagar meus servidores e configurações");
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = "Apagar meus servidores e configurações", FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = "Deixe desligado para manter tudo caso instale de novo.", Style = Dialogs.S("Caption"), Margin = new Thickness(0, 2, 0, 0) });
            keep.Content = label;
            body.Children.Add(keep);
            Button ok = Dialogs.Btn("Desinstalar", "DangerButton");
            Button cancel = Dialogs.Btn("Cancelar", "GhostButton");
            cancel.IsCancel = true;
            ok.Click += (s, e) => d.DialogResult = true;
            Dialogs.Layout(d, "Desinstalar VPN Panel", body, cancel, ok);
            if (d.ShowDialog() != true) return;
            try
            {
                Uninstall(keep.IsChecked == true);
                Dialogs.Info(null, "VPN Panel removido", "Pronto. O VPN Panel foi desinstalado.");
                DeleteFolderAfterExit();
            }
            catch (Exception ex) { Dialogs.Info(null, "Não foi possível desinstalar", ex.Message); }
        }
    }

    static class SetupProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            // --silent: update in place without the window (the running tunnel is left alone), then reopen the panel
            if (args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
            {
                Installer.Install(Res.Bytes("VpnPanel.exe"), Installer.HasDesktopShortcut);
                if (Paths.WireSock == null) WireSockSetup.Install(text => { });
                try { Process.Start(new ProcessStartInfo(Installer.Exe) { WorkingDirectory = Installer.Dir }).Dispose(); } catch { }
                return;
            }
            Application app = Installer.ThemedApp();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new SetupWindow().Window);
        }
    }

    class SetupWindow
    {
        public readonly Window Window;
        readonly StackPanel body;

        static Brush B(string key) { return Dialogs.B(key); }
        static Style S(string key) { return Dialogs.S(key); }

        public SetupWindow()
        {
            Window = new Window
            {
                Title = "Instalar VPN Panel",
                Width = 400,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.CanMinimize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = B("Bg"),
                Foreground = B("Text"),
                FontFamily = (FontFamily)Application.Current.FindResource("Font"),
                FontSize = 13,
                UseLayoutRounding = true,
            };
            Chrome.Apply(Window, 40);
            var frames = new IconBitmapDecoder(Res.Open("vpn.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            Window.Icon = frames.First(f => f.PixelWidth == 32);

            var root = new DockPanel();
            var titleBar = new Grid { Height = 40, Margin = new Thickness(16, 0, 6, 0) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new Image { Source = frames.First(f => f.PixelWidth == 32), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
            title.Children.Add(new TextBlock { Text = "Instalação do VPN Panel", Foreground = B("Muted"), FontSize = 12, FontWeight = FontWeights.SemiBold });
            titleBar.Children.Add(title);
            var close = new Button { Style = S("IconButton"), Content = "", FontSize = 10, Width = 34, Height = 30, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
            System.Windows.Automation.AutomationProperties.SetName(close, "Fechar");
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            close.Click += (s, e) => Window.Close();
            titleBar.Children.Add(close);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            body = new StackPanel { Margin = new Thickness(28, 8, 28, 26) };
            root.Children.Add(body);
            Window.Content = root;
            ShowWelcome(frames.First(f => f.PixelWidth == 256));
        }

        void ShowWelcome(ImageSource logo)
        {
            body.Children.Clear();
            body.Children.Add(new Image { Source = logo, Width = 72, Height = 72 });
            body.Children.Add(new TextBlock { Text = "VPN Panel", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });

            string installed = Installer.InstalledVersion;
            string versionText = installed == null ? "Versão " + Program.Version
                               : installed == Program.Version ? "Versão " + Program.Version + " (já instalada)"
                               : "Atualizar da versão " + installed + " para " + Program.Version;
            body.Children.Add(new TextBlock { Text = versionText, Style = S("Caption"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            body.Children.Add(new TextBlock
            {
                Text = "Conecte sua VPN WireGuard com um clique, escolha quais apps passam por ela e fique de olho na conexão.",
                Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(6, 12, 6, 0), LineHeight = 19,
            });

            var card = new Border { Style = S("Card"), Margin = new Thickness(0, 22, 0, 0), Padding = new Thickness(16, 14, 16, 14) };
            var desktop = new CheckBox { Style = S("Switch"), IsChecked = true };
            System.Windows.Automation.AutomationProperties.SetName(desktop, "Atalho na área de trabalho");
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = "Atalho na área de trabalho", FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = "O atalho no menu Iniciar é criado sempre.", Style = S("Caption"), Margin = new Thickness(0, 2, 0, 0) });
            desktop.Content = label;
            var options = new StackPanel();
            options.Children.Add(desktop);
            // WireSock is required; installed along unless it's already there
            bool needWireSock = Paths.WireSock == null;
            if (needWireSock)
            {
                var ws = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
                ws.Children.Add(new TextBlock { Text = "WireSock (necessário)", FontWeight = FontWeights.SemiBold });
                ws.Children.Add(new TextBlock { Text = "Programa que faz a conexão. É instalado junto, de graça para uso pessoal.", Style = S("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                options.Children.Add(ws);
            }
            card.Child = options;
            body.Children.Add(card);

            body.Children.Add(new TextBlock
            {
                Text = installed != null ? "Seus servidores e configurações são mantidos." : "Instala em " + Installer.Dir + ".",
                Style = S("Caption"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
            });

            var install = new Button
            {
                Style = S("PrimaryButton"),
                Content = installed == null ? "Instalar" : installed == Program.Version ? "Reinstalar" : "Atualizar",
                Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 16, 0, 0), IsDefault = true,
            };
            install.Click += (s, e) =>
            {
                install.IsEnabled = false;
                install.Content = "Instalando...";
                bool wantDesktop = desktop.IsChecked == true;
                byte[] exe = Res.Bytes("VpnPanel.exe");
                var worker = new Thread(() =>
                {
                    Exception error = null;
                    string wireSockError = null;
                    try { Installer.Install(exe, wantDesktop); } catch (Exception ex) { error = ex; }
                    if (error == null && needWireSock)
                        wireSockError = WireSockSetup.Install(text => Window.Dispatcher.BeginInvoke(new Action(() => install.Content = text)));
                    Window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (error == null) { ShowDone(wantDesktop, wireSockError); return; }
                        Dialogs.Info(Window, "Não foi possível instalar", error.Message);
                        install.IsEnabled = true;
                        install.Content = "Tentar de novo";
                    }));
                }) { IsBackground = true };
                worker.SetApartmentState(ApartmentState.STA); // WScript.Shell for the shortcuts
                worker.Start();
            };
            body.Children.Add(install);
        }

        void ShowDone(bool desktop, string wireSockError)
        {
            body.Children.Clear();
            var check = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = B("AccentSoft"), Margin = new Thickness(0, 6, 0, 0) };
            check.Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 30, Foreground = B("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(check);
            body.Children.Add(new TextBlock { Text = "Tudo pronto!", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) });
            body.Children.Add(new TextBlock
            {
                Text = "O VPN Panel está no menu Iniciar" + (desktop ? " e na área de trabalho." : "."),
                Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            });
            if (wireSockError != null)
                body.Children.Add(new TextBlock
                {
                    Text = "O WireSock não foi instalado agora. O VPN Panel tenta de novo quando você abrir.",
                    Style = S("Caption"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
                });
            var open = new Button { Style = S("PrimaryButton"), Content = "Abrir o VPN Panel", Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 24, 0, 0), IsDefault = true };
            open.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(Installer.Exe) { WorkingDirectory = Installer.Dir }); } catch { }
                Window.Close();
            };
            var close = new Button { Style = S("GhostButton"), Background = Brushes.Transparent, Content = "Fechar", Margin = new Thickness(0, 6, 0, 0), IsCancel = true };
            close.Click += (s, e) => Window.Close();
            body.Children.Add(open);
            body.Children.Add(close);
        }
    }
}
