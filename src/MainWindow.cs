// Main window (MainWindow.xaml) and tray icon: connection state, the four tabs and their actions.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
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
    class MainWindow
    {
        const int MaxReconnects = 3;

        readonly Window w;
        readonly Settings settings = Settings.Load();

        // Início (setup)
        readonly FrameworkElement pageSetup, step1Actions;
        readonly Border step1Badge, step2Badge;
        readonly TextBlock step1Mark, step2Mark, step1Done, step1Status, step1Error;
        readonly FrameworkElement step1Working, step1Failed;
        readonly RotateTransform step1SpinnerRotate;
        // Início
        readonly Border errorBanner;
        readonly TextBlock txtErrTitle, txtErrDetail;
        readonly Button btnPower;
        readonly TextBlock powerGlyph, txtStatus, txtScope, txtUptime;
        readonly TextBlock txtFlag, txtFlagGlobe, txtSrvName, txtSrvDetail;
        readonly TextBlock txtIp, txtLoc, txtOrg, txtIpNote;
        readonly Ellipse glow, ring, spinner;
        readonly RotateTransform spinnerRotate;
        // Servidores
        readonly StackPanel serverList, emptyServers;
        // Apps
        readonly TextBlock txtAppsCfg, txtModeHint, txtAppsSaved;
        readonly RadioButton segAll, segOnly, segExcept;
        readonly FrameworkElement appsAdd;
        readonly TextBox txtAppName;
        readonly Border appListCard, applyBanner;
        readonly StackPanel appList;
        // Opções
        readonly CheckBox chkReconnect, chkLock, chkStartup, chkAuto, chkSkipUac;
        // Tabs
        readonly RadioButton tabHome, tabServers, tabApps, tabOptions;
        readonly FrameworkElement pageHome, pageServers, pageApps, pageOptions;

        readonly WF.NotifyIcon tray;
        readonly WF.ToolStripMenuItem miStatus, miToggle;
        readonly SD.Icon trayOn, trayOff, trayWarn;
        readonly DispatcherTimer pollTimer, ipTimer, reconnectTimer;
        readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        bool exiting, hintShown, loading;
        bool? wasRunning;
        bool userStopped, connecting, reconnectPending;
        int reconnectAttempts;
        DateTime connectRequested, startedAt;
        Conn shown = Conn.Off;
        Action bannerPrimary, bannerSecondary;

        // handshake tracking from the WireSock log, for tunnels this app started
        bool monitoring, handshakeOk, sawHandshake;
        DateTime? pendingSince;
        long logPos;
        string logRest = "";
        string runningConfig;
        ConfigInfo active;
        bool wireSockInstalled, installingWireSock, triedWireSock;

        // servers arriving in Downloads (see WatchDownloads)
        FileSystemWatcher downloadsWatcher;
        DispatcherTimer downloadsTimer;
        readonly Dictionary<string, DateTime> pendingDownloads = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> handledDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime expectDownloadUntil;
        Window guide;
        string foundDownload;

        // apps editor
        AppMode editMode;
        List<string> editApps = new List<string>();
        bool appsNeedReconnect;

        T E<T>(string name) where T : class { return (T)w.FindName(name); }

        public MainWindow(bool startInTray)
        {
            w = (Window)XamlReader.Parse(Res.Text("MainWindow.xaml"));
            Chrome.Apply(w, 44);
            var frames = new IconBitmapDecoder(Res.Open("vpn.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            w.Icon = frames.First(f => f.PixelWidth == 32);
            E<Image>("TitleIcon").Source = frames.First(f => f.PixelWidth == 32);

            pageSetup = E<FrameworkElement>("PageSetup");
            step1Actions = E<FrameworkElement>("Step1Actions");
            step1Badge = E<Border>("Step1Badge");
            step2Badge = E<Border>("Step2Badge");
            step1Mark = E<TextBlock>("Step1Mark");
            step2Mark = E<TextBlock>("Step2Mark");
            step1Done = E<TextBlock>("Step1Done");
            step1Status = E<TextBlock>("Step1Status");
            step1Error = E<TextBlock>("Step1Error");
            step1Working = E<FrameworkElement>("Step1Working");
            step1Failed = E<FrameworkElement>("Step1Failed");
            step1SpinnerRotate = (RotateTransform)E<Ellipse>("Step1Spinner").RenderTransform;
            errorBanner = E<Border>("ErrorBanner");
            txtErrTitle = E<TextBlock>("TxtErrTitle");
            txtErrDetail = E<TextBlock>("TxtErrDetail");
            btnPower = E<Button>("BtnPower");
            powerGlyph = E<TextBlock>("PowerGlyph");
            txtStatus = E<TextBlock>("TxtStatus");
            txtScope = E<TextBlock>("TxtScope");
            txtUptime = E<TextBlock>("TxtUptime");
            txtFlag = E<TextBlock>("TxtFlag");
            txtFlagGlobe = E<TextBlock>("TxtFlagGlobe");
            txtSrvName = E<TextBlock>("TxtSrvName");
            txtSrvDetail = E<TextBlock>("TxtSrvDetail");
            txtIp = E<TextBlock>("TxtIp");
            txtLoc = E<TextBlock>("TxtLoc");
            txtOrg = E<TextBlock>("TxtOrg");
            txtIpNote = E<TextBlock>("TxtIpNote");
            glow = E<Ellipse>("Glow");
            ring = E<Ellipse>("Ring");
            spinner = E<Ellipse>("Spinner");
            spinnerRotate = (RotateTransform)spinner.RenderTransform;
            serverList = E<StackPanel>("ServerList");
            emptyServers = E<StackPanel>("EmptyServers");
            txtAppsCfg = E<TextBlock>("TxtAppsCfg");
            txtModeHint = E<TextBlock>("TxtModeHint");
            txtAppsSaved = E<TextBlock>("TxtAppsSaved");
            segAll = E<RadioButton>("SegAll");
            segOnly = E<RadioButton>("SegOnly");
            segExcept = E<RadioButton>("SegExcept");
            appsAdd = E<FrameworkElement>("AppsAdd");
            txtAppName = E<TextBox>("TxtAppName");
            appListCard = E<Border>("AppListCard");
            applyBanner = E<Border>("ApplyBanner");
            appList = E<StackPanel>("AppList");
            chkReconnect = E<CheckBox>("ChkReconnect");
            chkLock = E<CheckBox>("ChkLock");
            chkStartup = E<CheckBox>("ChkStartup");
            chkAuto = E<CheckBox>("ChkAuto");
            chkSkipUac = E<CheckBox>("ChkSkipUac");
            tabHome = E<RadioButton>("TabHome");
            tabServers = E<RadioButton>("TabServers");
            tabApps = E<RadioButton>("TabApps");
            tabOptions = E<RadioButton>("TabOptions");
            pageHome = E<FrameworkElement>("PageHome");
            pageServers = E<FrameworkElement>("PageServers");
            pageApps = E<FrameworkElement>("PageApps");
            pageOptions = E<FrameworkElement>("PageOptions");

            // title bar
            var btnMin = E<Button>("BtnMin");
            var btnClose = E<Button>("BtnClose");
            WindowChrome.SetIsHitTestVisibleInChrome(btnMin, true);
            WindowChrome.SetIsHitTestVisibleInChrome(btnClose, true);
            btnMin.Click += (s, e) => w.WindowState = WindowState.Minimized;
            btnClose.Click += (s, e) => w.Close();
            w.Closing += (s, e) =>
            {
                if (exiting) return;
                e.Cancel = true;
                w.Hide();
                if (!hintShown)
                {
                    tray.ShowBalloonTip(2000, "VPN Panel", "Continuo aqui na bandeja. Clique com o botão direito no ícone para sair.", WF.ToolTipIcon.Info);
                    hintShown = true;
                }
            };
            Application.Current.SessionEnding += (s, e) => Quit();

            // tabs
            tabHome.Checked += (s, e) => ShowPage(HomePage);
            tabServers.Checked += (s, e) => ShowPage(pageServers);
            tabApps.Checked += (s, e) => ShowPage(pageApps);
            tabOptions.Checked += (s, e) => ShowPage(pageOptions);

            // setup
            E<Button>("BtnInstallWireSock").Click += (s, e) => InstallWireSock();
            E<Button>("BtnGetWireSock").Click += (s, e) => OpenUrl("https://www.wiresock.net/");
            E<Button>("BtnSetupAdd").Click += (s, e) => BrowseImport();
            E<Button>("BtnSetupProton").Click += (s, e) => OpenGuide(false);
            E<Button>("BtnSetupHelp").Click += (s, e) => OpenGuide(true);
            E<Button>("BtnStep2Found").Click += (s, e) => { if (foundDownload != null) Import(new[] { foundDownload }); };

            // Início
            btnPower.Click += (s, e) => Toggle();
            E<Button>("BtnServerCard").Click += (s, e) => tabServers.IsChecked = true;
            E<Button>("BtnRefreshIp").Click += (s, e) => CheckIp();
            E<Button>("BtnRetry").Click += (s, e) => { Action a = bannerPrimary; HideBanner(); if (a != null) a(); };
            E<Button>("BtnErrLog").Click += (s, e) => { if (bannerSecondary != null) bannerSecondary(); };
            E<Button>("BtnErrClose").Click += (s, e) => HideBanner();

            // Servidores
            E<Button>("BtnImport").Click += (s, e) => BrowseImport();
            E<Button>("BtnEmptyHelp").Click += (s, e) => OpenGuide(false);
            E<Button>("BtnServersHelp").Click += (s, e) => OpenGuide(false);
            w.DragOver += (s, e) =>
            {
                e.Effects = DroppedConfs(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            w.Drop += (s, e) => Import(DroppedConfs(e));
            // Ctrl+V: a copied .conf file, or the config text itself
            w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox))
                    e.Handled = Paste();
            };

            // Apps
            segAll.Checked += (s, e) => SetMode(AppMode.All);
            segOnly.Checked += (s, e) => SetMode(AppMode.Only);
            segExcept.Checked += (s, e) => SetMode(AppMode.Except);
            E<Button>("BtnAddApp").Click += (s, e) => { AddApps(new[] { txtAppName.Text }); txtAppName.Clear(); txtAppName.Focus(); };
            txtAppName.KeyDown += (s, e) => { if (e.Key == Key.Enter) { AddApps(new[] { txtAppName.Text }); txtAppName.Clear(); } };
            E<Button>("BtnRunningApps").Click += (s, e) => AddApps(Dialogs.PickProcesses(w, AppIcon));
            E<Button>("BtnBrowseExe").Click += (s, e) => BrowseExe();
            E<Button>("BtnApplyNow").Click += (s, e) => Reconnect();

            // Opções
            loading = true;
            chkReconnect.IsChecked = settings.AutoReconnect;
            chkLock.IsChecked = settings.NetworkLock;
            chkAuto.IsChecked = settings.AutoConnect;
            chkSkipUac.IsChecked = settings.SkipUac;
            try { chkStartup.IsChecked = Tasks.Exists(Tasks.Logon); } catch { }
            loading = false;
            // Checked/Unchecked rather than Click, so screen readers' toggle works too
            Action<CheckBox, Action> onToggle = (c, a) =>
            {
                RoutedEventHandler h = (s, e) => { if (!loading) a(); };
                c.Checked += h;
                c.Unchecked += h;
            };
            onToggle(chkReconnect, SaveSettings);
            onToggle(chkLock, SaveSettings);
            onToggle(chkAuto, SaveSettings);
            onToggle(chkSkipUac, () => { SaveSettings(); SyncTasks(); });
            onToggle(chkStartup, ToggleStartup);
            E<Button>("BtnLog").Click += (s, e) => OpenLog();
            E<Button>("BtnDataDir").Click += (s, e) => Process.Start("explorer.exe", "\"" + Paths.Data + "\"");
            E<Button>("BtnQuit").Click += (s, e) => Quit();

            // tray
            trayOn = TrayIcon(SD.Color.FromArgb(47, 191, 113));
            trayOff = TrayIcon(SD.Color.FromArgb(110, 117, 130));
            trayWarn = TrayIcon(SD.Color.FromArgb(227, 165, 53));
            var menu = new WF.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = new SD.Font("Segoe UI", 9.5F) };
            miStatus = (WF.ToolStripMenuItem)menu.Items.Add("Desconectado");
            miStatus.Enabled = false;
            menu.Items.Add(new WF.ToolStripSeparator());
            miToggle = (WF.ToolStripMenuItem)menu.Items.Add("Conectar", null, (s, e) => Toggle());
            menu.Items.Add("Abrir VPN Panel", null, (s, e) => ShowWindow());
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Sair (a VPN continua como está)", null, (s, e) => Quit());
            foreach (WF.ToolStripItem item in menu.Items) item.Padding = new WF.Padding(6, 4, 6, 4);
            tray = new WF.NotifyIcon { Icon = trayOff, Text = "VPN Panel", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };
            tray.BalloonTipClicked += (s, e) => ShowWindow();

            wireSockInstalled = Paths.WireSock != null;
            UpdateAbout();

            RefreshConfigs();
            LoadAppsEditor();
            UpdateSetup();

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            pollTimer.Tick += (s, e) => Poll();
            // one-shot IP check a few seconds after connecting/disconnecting
            ipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            ipTimer.Tick += (s, e) => { ipTimer.Stop(); CheckIp(); };
            reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            reconnectTimer.Tick += (s, e) => { reconnectTimer.Stop(); Connect(); };

            ListenForShowRequests();
            WatchConfigs();
            WatchDownloads();
            SyncTasks();
            if (!startInTray) w.Show();
            w.Dispatcher.BeginInvoke(new Action(OnStart));
        }

        void OnStart()
        {
            Poll();
            pollTimer.Start();
            // WireSock is required: install it as soon as someone is looking (not on a silent tray start)
            if (w.IsVisible && !wireSockInstalled) InstallWireSock();
            if (settings.AutoConnect && wasRunning == false && Ready) Connect();
            else CheckIp();
        }

        // A second launch of the exe signals this event instead of opening another window
        void ListenForShowRequests()
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    ev.WaitOne();
                    w.Dispatcher.BeginInvoke(new Action(ShowWindow));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        FileSystemWatcher configWatcher;
        DispatcherTimer configDebounce;

        // Servers added, removed or renamed in the folder by hand show up without reopening the app
        void WatchConfigs()
        {
            configDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            configDebounce.Tick += (s, e) =>
            {
                configDebounce.Stop();
                RefreshConfigs();
                LoadAppsEditor();
                UpdateSetup();
            };
            configWatcher = new FileSystemWatcher(Paths.Configs, "*.conf") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (s, e) => w.Dispatcher.BeginInvoke(new Action(() => { configDebounce.Stop(); configDebounce.Start(); }));
            configWatcher.Created += changed;
            configWatcher.Deleted += changed;
            configWatcher.Changed += changed;
            configWatcher.Renamed += (s, e) => changed(s, e);
            configWatcher.EnableRaisingEvents = true;
        }

        // Keeps the scheduled tasks pointing at this exe (in the background: COM calls take a moment)
        void SyncTasks()
        {
            bool skip = settings.SkipUac;
            new Thread(() => Tasks.Sync(skip)) { IsBackground = true }.Start();
        }

        void ShowWindow()
        {
            w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            if (!wireSockInstalled && !triedWireSock) InstallWireSock();
        }

        void UpdateAbout()
        {
            string ws = Paths.WireSockVersion;
            E<TextBlock>("TxtAbout").Text = "VPN Panel " + Program.Version + "  ·  " + (ws != null ? "WireSock " + ws : "WireSock não instalado");
        }

        // Step 1 of the setup: winget (or the download from wiresock.net) in the background
        void InstallWireSock()
        {
            if (installingWireSock || wireSockInstalled) return;
            installingWireSock = triedWireSock = true;
            step1Status.Text = "Instalando o WireSock...";
            step1Working.Visibility = Visibility.Visible;
            step1Failed.Visibility = Visibility.Collapsed;
            step1SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            new Thread(() =>
            {
                string error = WireSockSetup.Install(text => w.Dispatcher.BeginInvoke(new Action(() => step1Status.Text = text)));
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    installingWireSock = false;
                    step1SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    if (error == null) { Poll(); return; }
                    step1Error.Text = error;
                    step1Working.Visibility = Visibility.Collapsed;
                    step1Failed.Visibility = Visibility.Visible;
                }));
            }) { IsBackground = true }.Start();
        }

        void Quit()
        {
            exiting = true;
            pollTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        void OpenLog()
        {
            if (File.Exists(Paths.Log)) Process.Start("notepad.exe", "\"" + Paths.Log + "\"");
            else Dialogs.Info(w, "Ainda não há log", "O log é criado na primeira conexão.");
        }

        // Ready = WireSock installed and at least one server; until then Início shows the setup steps
        bool Ready { get { return wireSockInstalled && active != null; } }

        FrameworkElement HomePage { get { return Ready ? pageHome : pageSetup; } }

        void ShowPage(FrameworkElement page)
        {
            foreach (FrameworkElement p in new[] { pageSetup, pageHome, pageServers, pageApps, pageOptions })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
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

        void SaveSettings()
        {
            settings.Config = active != null ? active.Name : null;
            settings.AutoReconnect = chkReconnect.IsChecked == true;
            settings.NetworkLock = chkLock.IsChecked == true;
            settings.AutoConnect = chkAuto.IsChecked == true;
            settings.SkipUac = chkSkipUac.IsChecked == true;
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

        // What the user sees. "Connected" needs a WireGuard handshake, not just a running client.
        enum Conn { Off, Connecting, Retrying, On, NoResponse }

        const int WarnAfter = 15, GiveUpAfter = 60; // seconds without a handshake response

        Conn State
        {
            get
            {
                // being stopped: the process lingers until the next poll
                if (wasRunning == true && userStopped) return reconnectPending ? Conn.Connecting : Conn.Off;
                if (wasRunning == true)
                {
                    if (!monitoring) return Conn.On; // started outside the app: no log to check
                    if (pendingSince != null && (DateTime.Now - pendingSince.Value).TotalSeconds > WarnAfter) return Conn.NoResponse;
                    return handshakeOk ? Conn.On : Conn.Connecting;
                }
                if (reconnectTimer != null && reconnectTimer.IsEnabled) return Conn.Retrying;
                return connecting ? Conn.Connecting : Conn.Off;
            }
        }

        void Toggle()
        {
            if (reconnectTimer.IsEnabled) { reconnectTimer.Stop(); UpdateState(); return; }
            if (wasRunning == true || connecting) Disconnect(true); else Connect();
        }

        void Connect()
        {
            HideBanner();
            if (!Ready)
            {
                ShowWindow();
                tabHome.IsChecked = true;
                ShowPage(HomePage);
                return;
            }
            // WireSock's own app (with UI) may already hold a tunnel through its service
            if (Paths.WireSockServiceRunning)
            {
                ShowWindow();
                if (!Dialogs.Confirm(w, "O WireSock já está conectado",
                        "O aplicativo do WireSock está com uma conexão ativa. Para conectar pelo VPN Panel, essa conexão precisa ser desligada.",
                        "Desligar e conectar", false))
                    return;
                try { Paths.StopWireSockService(); }
                catch (Exception ex)
                {
                    Dialogs.Info(w, "Não foi possível desligar", "Desconecte pelo aplicativo do WireSock e tente de novo.\n\n" + ex.Message);
                    return;
                }
            }
            Tunnel.Start(Paths.WireSock, active.Name, settings.NetworkLock);
            SaveSettings();
            connecting = true;
            connectRequested = DateTime.Now;
            StartMonitoring(true);
            appsNeedReconnect = false;
            UpdateAppsSaved();
            UpdateState();
        }

        // byUser: the user asked for it (resets the auto-reconnect budget)
        void Disconnect(bool byUser)
        {
            userStopped = true;
            connecting = false;
            monitoring = false;
            if (byUser) reconnectAttempts = 0;
            Tunnel.Stop();
            ipTimer.Stop();
            ipTimer.Start();
        }

        void Reconnect()
        {
            if (wasRunning != true) { Connect(); return; }
            reconnectPending = true;
            Disconnect(false);
        }

        // Follows the WireSock log (written at -log-level info) for handshake events
        void StartMonitoring(bool fresh)
        {
            monitoring = true;
            handshakeOk = false;
            sawHandshake = false;
            pendingSince = fresh ? (DateTime?)DateTime.Now : null;
            logPos = 0;
            logRest = "";
        }

        void ReadHandshakes()
        {
            try
            {
                using (var fs = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < logPos) { logPos = 0; logRest = ""; } // log was recreated
                    if (fs.Length == logPos) return;
                    fs.Seek(logPos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - logPos];
                    int n = fs.Read(buf, 0, buf.Length);
                    logPos += n;
                    string text = logRest + Encoding.UTF8.GetString(buf, 0, n);
                    int cut = text.LastIndexOf('\n');
                    logRest = cut >= 0 ? text.Substring(cut + 1) : text;
                    if (cut < 0) return;
                    foreach (string line in text.Substring(0, cut).Split('\n'))
                    {
                        if (line.Contains("Handshake response received")) { handshakeOk = sawHandshake = true; pendingSince = null; }
                        else if (line.Contains("Handshake initiation sent"))
                        {
                            sawHandshake = true;
                            if (pendingSince == null) pendingSince = DateTime.Now;
                        }
                    }
                }
            }
            catch { }
        }

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
                using (var r = new StreamReader(fs))
                {
                    string last = r.ReadToEnd().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("\"log_level\":\"error\""));
                    Match m = last != null ? Regex.Match(last, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"") : Match.Empty;
                    if (m.Success) message = Regex.Replace(m.Groups[1].Value, @"\[\w+\]:\s*(Unhandled exception\s*)?", "").Trim();
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
            if (lower.Contains("driver") || lower.Contains("adapter") || lower.Contains("wintun") || lower.Contains("ndis"))
                return "O WireSock não conseguiu preparar a rede. Reinstale o WireSock e reinicie o computador.";
            if (lower.Contains("access") && lower.Contains("denied"))
                return "O Windows negou permissão ao WireSock. Feche e abra o VPN Panel de novo.";
            return message.Length > 140 ? message.Substring(0, 140) + "..." : message;
        }

        void Poll()
        {
            if (wireSockInstalled != (Paths.WireSock != null))
            {
                wireSockInstalled = !wireSockInstalled;
                UpdateAbout();
                UpdateSetup();
            }

            Process[] procs = Tunnel.Find();
            Process p = procs.FirstOrDefault();
            bool running = p != null;

            if (running != wasRunning)
            {
                if (running)
                {
                    string cmd = Tunnel.CommandLine(p.Id);
                    runningConfig = Tunnel.ConfigOf(cmd);
                    try { startedAt = p.StartTime; } catch { startedAt = DateTime.Now; }
                    connecting = false;
                    // a tunnel this app started before it was reopened still logs to our file
                    if (!monitoring && cmd != null && cmd.Contains("-log-level info") && File.Exists(Paths.Log) &&
                        File.GetLastWriteTime(Paths.Log) >= startedAt.AddSeconds(-5))
                        StartMonitoring(false);
                    // follow tunnels started elsewhere (e.g. a .bat file)
                    if (runningConfig != null && (active == null || active.Name != runningConfig) && File.Exists(Paths.Conf(runningConfig)))
                    {
                        settings.Config = runningConfig;
                        RefreshConfigs();
                        LoadAppsEditor();
                    }
                }
                else if (wasRunning == true)
                {
                    bool quick = (DateTime.Now - startedAt).TotalSeconds < 10;
                    monitoring = false;
                    if (!userStopped && !reconnectPending)
                    {
                        if (quick) ShowError("Não foi possível conectar", LogTail());
                        else if (settings.AutoReconnect && reconnectAttempts < MaxReconnects)
                        {
                            reconnectAttempts++;
                            reconnectTimer.Start();
                        }
                        else ShowError("A conexão caiu", reconnectAttempts > 0 ? "Tentei reconectar " + reconnectAttempts + " vezes sem sucesso. " + LogTail() : LogTail());
                    }
                    userStopped = false;
                    runningConfig = null;
                }
                wasRunning = running;
                if (!running && reconnectPending)
                {
                    reconnectPending = false;
                    Connect();
                }
                RefreshConfigs();
            }

            if (running && monitoring)
            {
                ReadHandshakes();
                // a WireSock version that logs handshakes differently: don't judge the connection by the log
                if (!sawHandshake && (DateTime.Now - startedAt).TotalSeconds > 10)
                {
                    monitoring = false;
                    pendingSince = null;
                }
                // no handshake response for too long: the server is down or blocked
                if (pendingSince != null && (DateTime.Now - pendingSince.Value).TotalSeconds > GiveUpAfter)
                {
                    // it worked before: a stall, worth retrying; never worked: wrong or dead server
                    if (handshakeOk && settings.AutoReconnect && reconnectAttempts < MaxReconnects)
                    {
                        reconnectAttempts++;
                        Reconnect();
                    }
                    else
                    {
                        Disconnect(false);
                        ShowBanner(false, "O servidor não respondeu",
                            "Ele pode estar fora do ar ou bloqueado pela sua rede. Tente de novo ou escolha outro servidor.",
                            "Tentar de novo", Connect, "Trocar servidor", () => tabServers.IsChecked = true);
                    }
                }
            }

            // the client exited before a poll saw it
            if (!running && connecting && (DateTime.Now - connectRequested).TotalSeconds > 8)
            {
                connecting = false;
                ShowError("Não foi possível conectar", LogTail());
            }

            Conn now = State;
            if (now != shown)
            {
                if (now == Conn.On) { HideBanner(); CheckIp(); } // check the new route once traffic flows
                if (now == Conn.NoResponse)
                    ShowBanner(true, "O servidor não está respondendo",
                        settings.NetworkLock ? "Continuo tentando. Enquanto isso, o kill switch mantém a internet bloqueada."
                                             : "Continuo tentando. Se não voltar em instantes, escolha outro servidor.",
                        "Trocar servidor", () => tabServers.IsChecked = true, "Desconectar", () => Disconnect(true));
                else if (shown == Conn.NoResponse && errorBanner.Visibility == Visibility.Visible && now != Conn.Off) HideBanner();
                UpdateState();
            }

            if (running)
            {
                TimeSpan up = DateTime.Now - startedAt;
                txtUptime.Text = string.Format("Conectado há {0:00}:{1:00}:{2:00}", (int)up.TotalHours, up.Minutes, up.Seconds);
                if (up.TotalSeconds > 60 && now == Conn.On) reconnectAttempts = 0;
            }
            foreach (Process x in procs) x.Dispose();
        }

        void UpdateState()
        {
            Conn st = State;
            shown = st;
            bool on = st == Conn.On;
            bool busy = st == Conn.Connecting || st == Conn.Retrying || st == Conn.NoResponse;
            spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
                spinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            else spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);

            string status, scope;
            if (on)
            {
                btnPower.Background = B("Accent");
                powerGlyph.Foreground = B("OnAccent");
                ring.Stroke = B("Accent");
                txtStatus.Foreground = B("Accent");
                status = "Conectado";
                scope = active == null || active.Mode == AppMode.All ? "Todo o tráfego do PC está protegido"
                      : active.Mode == AppMode.Only ? "Só " + JoinPt(active.Apps) + " passa pela VPN"
                      : "Tudo passa pela VPN, exceto " + JoinPt(active.Apps);
            }
            else
            {
                btnPower.Background = B("Surface2");
                powerGlyph.Foreground = busy ? B("Warn") : B("Muted");
                ring.Stroke = st == Conn.NoResponse ? B("Warn") : B("Line");
                txtStatus.Foreground = st == Conn.NoResponse ? B("Warn") : B("Text");
                switch (st)
                {
                    case Conn.NoResponse:
                        status = "Sem resposta";
                        scope = "O servidor não está respondendo";
                        break;
                    case Conn.Retrying:
                        status = "Reconectando...";
                        scope = "A conexão caiu. Tentativa " + reconnectAttempts + " de " + MaxReconnects;
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
            txtUptime.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            bool stoppable = wasRunning == true || busy;
            btnPower.ToolTip = stoppable ? "Desconectar" : "Conectar";

            tray.Icon = on ? trayOn : st == Conn.NoResponse ? trayWarn : trayOff;
            string tip = "VPN Panel: " + status.TrimEnd('.').ToLowerInvariant() + (on && active != null ? " (" + active.Name + ")" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            miStatus.Text = on && active != null ? "Conectado · " + active.Name : status;
            miToggle.Text = stoppable ? "Desconectar" : "Conectar";

            glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 0.28 : 0, TimeSpan.FromMilliseconds(400)));
            bool partial = on && active != null && active.Mode == AppMode.Only;
            txtIpNote.Text = partial ? "Este é o IP dos apps fora da VPN. " + JoinPt(active.Apps) + " usa o IP do servidor." : "";
            txtIpNote.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppsSaved();
        }

        void CheckIp()
        {
            txtIp.Text = "...";
            txtLoc.Text = "...";
            txtOrg.Text = "...";
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers["User-Agent"] = "vpn-panel";
            wc.DownloadStringCompleted += (s, e) =>
            {
                wc.Dispose();
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

        void RefreshConfigs()
        {
            string[] names = Paths.ConfigNames();
            if (settings.Config == null || !names.Contains(settings.Config, StringComparer.OrdinalIgnoreCase))
            {
                settings.Config = names.FirstOrDefault();
                settings.Save();
            }
            active = settings.Config != null ? ConfigInfo.Read(names.First(n => n.Equals(settings.Config, StringComparison.OrdinalIgnoreCase))) : null;

            serverList.Children.Clear();
            foreach (string n in names)
            {
                ConfigInfo c = n == active.Name ? active : ConfigInfo.Read(n);
                if (c.Endpoint != null && c.Place == null) Geo.Lookup(c.Endpoint, RefreshConfigs);
                serverList.Children.Add(ServerCard(c));
            }
            emptyServers.Visibility = names.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            Geo.Place place = active != null ? active.Place : null;
            txtFlag.Text = place != null ? place.Country : "";
            txtFlagGlobe.Visibility = place != null ? Visibility.Collapsed : Visibility.Visible;
            txtSrvName.Text = active != null ? active.Name : "—";
            txtSrvDetail.Text = active == null ? "" : ServerDetail(active);
            txtSrvDetail.ToolTip = active == null ? null : active.Detail;
            if (txtStatus != null && wasRunning != null) UpdateState();
        }

        // "Houston, Estados Unidos" once the location is known; server label and IP until then
        static string ServerDetail(ConfigInfo c)
        {
            Geo.Place p = c.Readable ? c.Place : null;
            if (p == null) return c.Detail;
            return string.IsNullOrEmpty(p.City) ? p.CountryName : p.City + ", " + p.CountryName;
        }

        UIElement ServerCard(ConfigInfo c)
        {
            bool isActive = active != null && c.Name == active.Name;
            bool live = IsLive(c.Name);

            var card = new Button
            {
                Style = Dialogs.S("CardButton"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 12, 6, 12),
            };
            AutomationProperties.SetName(card, "Usar " + c.Name);
            if (isActive) { card.BorderBrush = B("Accent"); card.Background = B("ActiveCard"); }

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Geo.Place place = c.Place;
            var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = isActive ? B("AccentSoft") : B("Surface2"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
            badge.Child = place != null
                ? new TextBlock { Text = place.Country, FontWeight = FontWeights.Bold, Foreground = isActive ? B("Accent") : B("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 16, Foreground = isActive ? B("Accent") : B("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(badge);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = c.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
            if (live || isActive)
            {
                var pill = new Border { Background = live ? B("AccentSoft") : B("Hover"), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pill.Child = new TextBlock { Text = live ? "Conectado" : "Selecionado", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = live ? B("Accent") : B("Muted") };
                titleRow.Children.Add(pill);
            }
            text.Children.Add(titleRow);
            text.Children.Add(new TextBlock { Text = ServerDetail(c), ToolTip = c.Detail, Style = Dialogs.S("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            if (c.Readable && c.Mode != AppMode.All)
                text.Children.Add(new TextBlock { Text = (c.Mode == AppMode.Only ? "Só " : "Exceto ") + JoinPt(c.Apps), Style = Dialogs.S("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var rename = new Button { Style = Dialogs.S("IconButton"), Content = "", ToolTip = "Renomear" };
            var delete = new Button { Style = Dialogs.S("IconDangerButton"), Content = "", ToolTip = "Excluir" };
            AutomationProperties.SetName(rename, "Renomear " + c.Name);
            AutomationProperties.SetName(delete, "Excluir " + c.Name);
            rename.Click += (s, e) => { e.Handled = true; RenameConfig(c.Name); };
            delete.Click += (s, e) => { e.Handled = true; DeleteConfig(c.Name); };
            actions.Children.Add(rename);
            actions.Children.Add(delete);
            Grid.SetColumn(actions, 2);
            g.Children.Add(actions);

            card.Content = g;
            card.Click += (s, e) => SelectConfig(c.Name);
            return card;
        }

        bool IsLive(string name)
        {
            return wasRunning == true && runningConfig != null && runningConfig.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        void SelectConfig(string name)
        {
            if (active != null && active.Name == name) { tabHome.IsChecked = true; return; }
            settings.Config = name;
            settings.Save();
            RefreshConfigs();
            LoadAppsEditor();
            // already connected: switch servers right away
            if (wasRunning == true) Reconnect();
            tabHome.IsChecked = true;
        }

        void RenameConfig(string name)
        {
            if (IsLive(name)) { Dialogs.Info(w, "Servidor em uso", "Desconecte a VPN antes de renomear este servidor."); return; }
            string newName = Dialogs.Prompt(w, "Renomear servidor", null, name, n => ConfFile.CheckName(n, name));
            if (newName == null || newName == name) return;
            try
            {
                ConfFile.Rename(name, newName);
                if (settings.Config == name) { settings.Config = newName; settings.Save(); }
            }
            catch (Exception ex) { Dialogs.Info(w, "Não foi possível renomear", ex.Message); }
            RefreshConfigs();
            LoadAppsEditor();
        }

        void DeleteConfig(string name)
        {
            if (IsLive(name)) { Dialogs.Info(w, "Servidor em uso", "Desconecte a VPN antes de excluir este servidor."); return; }
            if (!Dialogs.Confirm(w, "Excluir " + name + "?", "O arquivo vai para a Lixeira do Windows, então dá para recuperar depois.", "Excluir", true)) return;
            try { ConfFile.Delete(name); }
            catch (Exception ex) { Dialogs.Info(w, "Não foi possível excluir", ex.Message); }
            RefreshConfigs();
            LoadAppsEditor();
            UpdateSetup();
            UpdateState();
        }

        static IEnumerable<string> DroppedConfs(DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            return (files ?? new string[0]).Where(ConfFile.IsImportable);
        }

        void BrowseImport()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Adicionar servidor",
                Filter = "Arquivo de servidor WireGuard (*.conf, *.zip)|*.conf;*.zip",
                Multiselect = true,
            };
            if (ofd.ShowDialog(w) == true) Import(ofd.FileNames);
        }

        void Import(IEnumerable<string> files)
        {
            var errors = new List<string>();
            var added = new List<string>();
            foreach (string f in files)
            {
                try { added.AddRange(ConfFile.Import(f)); }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(f) + ": " + ex.Message); }
            }
            Imported(added, errors);
        }

        void Imported(List<string> added, List<string> errors)
        {
            bool wasReady = Ready;
            if (added.Count > 0 && active == null) settings.Config = added[0];
            RefreshConfigs();
            LoadAppsEditor();
            UpdateSetup();
            UpdateState();
            // finishing setup lands on Início, ready to connect; otherwise show the new server in the list
            if (added.Count > 0) { if (!wasReady && Ready) tabHome.IsChecked = true; else tabServers.IsChecked = true; }
            if (errors.Count > 0) Dialogs.Info(w, added.Count > 0 ? "Alguns arquivos não foram adicionados" : "Arquivo não adicionado", string.Join("\n", errors));
        }

        // Ctrl+V on the window; false when the clipboard has nothing to import
        bool Paste()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(ConfFile.IsImportable).ToList();
                    if (files.Count == 0) return false;
                    Import(files);
                    return true;
                }
                string text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (!ConfFile.IsWireGuard(text)) return false;
                string name = Dialogs.Prompt(w, "Servidor colado", "Dê um nome para este servidor:", PastedName(text), n => ConfFile.CheckName(n, null));
                if (name != null) Imported(new List<string> { ConfFile.ImportText(text, name) }, new List<string>());
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Info(w, "Não deu para colar", ex.Message);
                return true;
            }
        }

        // The endpoint's host name (node-br-05 from node-br-05.example.net), or "Servidor"
        static string PastedName(string text)
        {
            Match m = Regex.Match(text, @"^\s*Endpoint\s*=\s*([A-Za-z][\w-]*)\.", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            string baseName = m.Success ? m.Groups[1].Value : "Servidor";
            string name = baseName;
            for (int n = 2; File.Exists(Paths.Conf(name)); n++) name = baseName + " " + n;
            return name;
        }

        // ---------- servers arriving in Downloads ----------

        // Expect a download during setup, and for a while after the guide was opened
        bool ExpectingDownload { get { return active == null || DateTime.Now < expectDownloadUntil; } }

        void OpenGuide(bool otherProvider)
        {
            if (guide != null) { guide.Activate(); return; }
            expectDownloadUntil = DateTime.Now.AddMinutes(20);
            guide = Dialogs.ProviderGuide(w, otherProvider);
            bool browse = guide.ShowDialog() == true;
            guide = null;
            if (browse) BrowseImport();
        }

        void WatchDownloads()
        {
            string dir = Paths.Downloads;
            if (dir == null || !Directory.Exists(dir)) return;
            downloadsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            downloadsTimer.Tick += (s, e) => CheckDownloads();
            try
            {
                downloadsWatcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler seen = (s, e) => w.Dispatcher.BeginInvoke(new Action(() => OnDownload(e.FullPath)));
                downloadsWatcher.Created += seen;
                downloadsWatcher.Changed += seen;
                downloadsWatcher.Renamed += (s, e) => w.Dispatcher.BeginInvoke(new Action(() => OnDownload(e.FullPath)));
                downloadsWatcher.EnableRaisingEvents = true;
            }
            catch { downloadsWatcher = null; }
        }

        static string DownloadKey(string path)
        {
            try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks; } catch { return path; }
        }

        void OnDownload(string path)
        {
            if (downloadsTimer == null || !ExpectingDownload || !ConfFile.IsImportable(path) || handledDownloads.Contains(DownloadKey(path))) return;
            if (!pendingDownloads.ContainsKey(path)) pendingDownloads[path] = DateTime.Now;
            downloadsTimer.Start();
        }

        // Browsers write the file in steps (empty file, .part, rename): import once it reads as a server
        void CheckDownloads()
        {
            foreach (string path in pendingDownloads.Keys.ToList())
            {
                bool ready = false;
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } // fails while still being written
                    ready = ConfFile.ReadConfs(path).Count > 0;
                }
                catch { }
                if (ready)
                {
                    pendingDownloads.Remove(path);
                    handledDownloads.Add(DownloadKey(path));
                    Downloaded(path);
                }
                else if ((DateTime.Now - pendingDownloads[path]).TotalSeconds > 30) pendingDownloads.Remove(path);
            }
            if (pendingDownloads.Count == 0) downloadsTimer.Stop();
        }

        void Downloaded(string path)
        {
            if (guide != null) guide.Close();
            int before = Paths.ConfigNames().Length;
            ShowWindow();
            Import(new[] { path });
            int added = Paths.ConfigNames().Length - before;
            if (added > 0)
                tray.ShowBalloonTip(3000, "VPN Panel", added == 1 ? "Servidor adicionado. Pronto para conectar." : added + " servidores adicionados.", WF.ToolTipIcon.Info);
        }

        // Newest server file in Downloads from the last week, if any
        static string RecentDownload()
        {
            try
            {
                string dir = Paths.Downloads;
                if (dir == null) return null;
                return new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => ConfFile.IsImportable(f.Name) && f.LastWriteTime > DateTime.Now.AddDays(-7))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(10)
                    .Select(f => f.FullName)
                    .FirstOrDefault(f => { try { return ConfFile.ReadConfs(f).Count > 0; } catch { return false; } });
            }
            catch { return null; }
        }

        // ---------- apps (split tunneling) ----------

        void LoadAppsEditor()
        {
            editApps = active != null ? new List<string>(active.Apps) : new List<string>();
            editMode = active != null ? active.Mode : AppMode.All;
            txtAppsCfg.Text = active != null ? "Servidor: " + active.Name : "Adicione um servidor primeiro";
            loading = true;
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

        // Saves the editor to the config right away (unless the chosen mode still has an empty list)
        void PersistApps()
        {
            if (active == null) return;
            if (editMode != AppMode.All && editApps.Count == 0) { UpdateAppsSaved(); return; }
            try { ConfFile.WriteApps(Paths.Conf(active.Name), editMode, editMode == AppMode.All ? new List<string>() : editApps); }
            catch (Exception ex) { Dialogs.Info(w, "Não foi possível salvar", ex.Message); return; }
            if (IsLive(active.Name)) appsNeedReconnect = true;
            RefreshConfigs();
        }

        void UpdateAppsSaved()
        {
            if (applyBanner == null || active == null) return;
            bool incomplete = editMode != AppMode.All && editApps.Count == 0;
            bool showApply = appsNeedReconnect && IsLive(active.Name) && !incomplete;
            applyBanner.Visibility = showApply ? Visibility.Visible : Visibility.Collapsed;
            txtAppsSaved.Visibility = showApply ? Visibility.Collapsed : Visibility.Visible;
            txtAppsSaved.Text = incomplete ? "Adicione pelo menos um app para usar este modo." : "As mudanças são salvas automaticamente.";
            txtAppsSaved.Foreground = incomplete ? B("Warn") : B("Muted");
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

        // Icon of a program, from the given path or from a running process with that name
        ImageSource AppIcon(string name, string path)
        {
            ImageSource cached;
            if (iconCache.TryGetValue(name, out cached) && cached != null) return cached;
            ImageSource img = null;
            try
            {
                if (path == null)
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { if (path == null) path = p.MainModule.FileName; } catch { }
                        p.Dispose();
                    }
                }
                if (path != null)
                {
                    using (SD.Icon icon = SD.Icon.ExtractAssociatedIcon(path))
                    {
                        img = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                    }
                }
            }
            catch { }
            iconCache[name] = img;
            return img;
        }

        // ---------- options ----------

        void ToggleStartup()
        {
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

        // ---------- tray ----------

        // App mark (white shield on a colored disc) at the tray's icon size
        static SD.Icon TrayIcon(SD.Color disc)
        {
            SD.Size size = WF.SystemInformation.SmallIconSize;
            var bmp = new SD.Bitmap(size.Width, size.Height);
            using (SD.Graphics g = SD.Graphics.FromImage(bmp))
            using (var brush = new SD.SolidBrush(disc))
            using (var path = new SD.Drawing2D.GraphicsPath())
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.ScaleTransform(size.Width / 32F, size.Height / 32F);
                g.FillEllipse(brush, 0.5F, 0.5F, 31, 31);
                path.AddLine(16, 6, 24, 9);
                path.AddLine(24, 9, 24, 15);
                path.AddBezier(24, 15, 24, 20, 20, 23.5F, 16, 26);
                path.AddBezier(16, 26, 12, 23.5F, 8, 20, 8, 15);
                path.AddLine(8, 15, 8, 9);
                path.CloseFigure();
                g.FillPath(SD.Brushes.White, path);
            }
            return SD.Icon.FromHandle(bmp.GetHicon());
        }

        class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? SD.Color.FromArgb(233, 236, 241) : SD.Color.FromArgb(138, 147, 163);
                base.OnRenderItemText(e);
            }
        }

        class DarkMenuColors : WF.ProfessionalColorTable
        {
            static readonly SD.Color Bg = SD.Color.FromArgb(22, 25, 32);
            static readonly SD.Color Hover = SD.Color.FromArgb(39, 44, 55);
            static readonly SD.Color Line = SD.Color.FromArgb(38, 43, 53);
            public override SD.Color ToolStripDropDownBackground { get { return Bg; } }
            public override SD.Color ImageMarginGradientBegin { get { return Bg; } }
            public override SD.Color ImageMarginGradientMiddle { get { return Bg; } }
            public override SD.Color ImageMarginGradientEnd { get { return Bg; } }
            public override SD.Color MenuBorder { get { return Line; } }
            public override SD.Color MenuItemBorder { get { return Hover; } }
            public override SD.Color MenuItemSelected { get { return Hover; } }
            public override SD.Color SeparatorDark { get { return Line; } }
            public override SD.Color SeparatorLight { get { return Line; } }
        }
    }
}
