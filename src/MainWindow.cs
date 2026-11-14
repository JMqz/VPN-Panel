// Main window (MainWindow.xaml) and tray icon: connection state, the four tabs and their actions.

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
        readonly Ellipse statusRing, statusSpinner;
        readonly RotateTransform spinnerRotate;
        readonly System.Windows.Shapes.Path shieldBg, ravenBeak, ravenHead, ravenBody, ravenTail;
        readonly Ellipse ravenEye;
        int connectSeq;
        readonly TextBlock txtStatus, txtScope, txtUptime, txtDown, txtUp, txtDownIcon;
        readonly WrapPanel homeAppsPanel;
        readonly FrameworkElement trafficRow;
        int downKey = -1, upKey = -1, totalKey = -1; // what the traffic labels show now (see SpeedKey)
        string totalText = "";
        readonly TextBlock txtFlag, txtFlagGlobe, txtSrvName, txtSrvDetail, txtSrvPing;
        readonly TextBlock txtIp, txtLoc, txtOrg, txtIpNote;
        readonly Ellipse glow;
        readonly Image titleIcon;
        // Servidores
        readonly StackPanel serverList, emptyServers;
        readonly TextBox txtServerSearch;
        readonly Button btnClearServerSearch;
        readonly FrameworkElement serverSearchBox;
        readonly Button btnFastest;
        bool connectingFastestPending;
        DateTime fastestStarted;
        readonly DispatcherTimer fastestTimer;
        // Apps
        readonly TextBlock txtAppsCfg, txtModeHint, txtAppsSaved;
        readonly CheckBox chkSharedApps;
        readonly RadioButton segAll, segOnly, segExcept;
        readonly FrameworkElement appsAdd;
        readonly TextBox txtAppName;
        readonly Border appListCard, applyBanner;
        readonly StackPanel appList;
        // Opções
        readonly CheckBox chkReconnect, chkLock, chkMtu, chkAutoHeal, chkHotkey, chkStartup, chkAuto, chkSkipUac, chkUpdates;
        readonly TextBlock txtLockNote;
        readonly Border cardUpdate;
        readonly Button btnApplyUpdate, btnCheckUpdate, btnResetLock;
        readonly TextBlock txtUpdateFoundTitle, txtUpdateFoundDetail, txtUpdateBadge;
        Updater.ReleaseInfo availableUpdate;
        bool pendingUpdatePrompt, updateDialogOpen, checkingForUpdates;
        DateTime lastUpdateCheck = DateTime.MinValue;
        readonly DispatcherTimer updateCheckTimer;
        // Tabs
        readonly RadioButton tabHome, tabServers, tabApps, tabDiag, tabOptions;
        readonly FrameworkElement pageHome, pageServers, pageApps, pageDiag, pageOptions;
        // Testes & Saúde (PageDiag)
        readonly Border diagRouteBadge, cardDiagVerdict, cardDiagLive;
        readonly Ellipse diagVerdictDot, chkItem1Dot, chkItem2Dot, chkItem3Dot, chkItem4Dot, chkItem5Dot, chkItem6Dot, chkItem7Dot;
        readonly TextBlock txtDiagRouteBadge, txtDiagVerdict, txtDiagVerdictSub;
        readonly TextBlock txtDiagLiveStatus, txtDiagLiveTime, txtDiagSentLoss, txtDiagPing, txtDiagJitter, txtDiagDrops, txtDiagGw, txtDiagEp;
        readonly TextBlock txtChk1Detail, txtChk2Detail, txtChk3Detail, txtChk4Detail, txtChk5Detail, txtChk6Detail, txtChk7Detail;
        readonly TextBlock txtDiagTimelineEmpty;
        readonly StackPanel diagTimelineList;
        readonly Button btnDiagQuick, btnDiagContinuous, btnDiagRepair, btnDiagCopy, btnDiagApps, btnDiagClearLog;

        readonly WF.NotifyIcon tray;
        readonly WF.ContextMenuStrip trayMenu;
        readonly WF.ToolStripMenuItem miStatus, miToggle, miAllServers;
        readonly WF.ToolStripSeparator miServersEnd;
        readonly List<WF.ToolStripItem> miRecent = new List<WF.ToolStripItem>();
        string trayMenuKey; // what the server items were built from (see UpdateTrayServers)
        SD.Icon trayOn, trayOff, trayWarn;
        readonly WrapPanel panelColorPresets;
        ColorThemePreset activePreset;
        static SD.Color activeGdiAccent = SD.Color.FromArgb(68, 173, 77);
        readonly DispatcherTimer pollTimer, ipTimer, reconnectTimer, geoDebounce;
        readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> iconMisses = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        Microsoft.Win32.PowerModeChangedEventHandler powerModeHandler;
        System.Net.NetworkInformation.NetworkAddressChangedEventHandler networkAddressHandler;
        System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler networkAvailHandler;

        bool exiting, hintShown, loading, serversDirty, appsDirty, ipDirty;
        bool? wasRunning;
        bool userStopped, connecting, reconnectPending, pendingPreConnectCleanup;
        int reconnectAttempts, postDropCleanupSeq;
        uint msgTaskbarCreated;
        IntPtr windowHandle;
        bool hotkeyRegistered;

        const int HotkeyId = 9001;
        const uint ModAlt = 0x0001;
        const uint ModControl = 0x0002;
        const uint VkV = 0x56;
        const int WmHotkey = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string lpString);
        DateTime startedAt, lastProcScan, lastHomePing, lastHomeAppsCheck;
        long connectTotalStartTick, connectLaunchTick, resumeGraceUntilTick, lastDropTick;
        long rehandshakeSuspectTick;
        int rehandshakeGen;
        bool connectPausedForDialog, activeProbeRunning, rehandshakeConfirmedDown, rehandshakeOnlyChecked10s;
        bool wireSockScanRunning, cmdLineLookupRunning, runningAppsScanRunning;
        long lastWireSockBgCheckTick;
        string lastHomeAppsRunningKey = "";
        Conn shown = Conn.Off;
        Action bannerPrimary, bannerSecondary;

        // handshake tracking from the WireSock log, for tunnels this app started
        bool monitoring, handshakeOk, sawHandshake, seekToTail;
        bool step1SpinnerRunning, powerSpinnerRunning, startupIsolationCheckPending;
        long? pendingSinceTick;

        static long NowTicks() { return Stopwatch.GetTimestamp(); }

        static double ElapsedSeconds(long startTicks)
        {
            if (startTicks <= 0) return 0;
            return (double)(Stopwatch.GetTimestamp() - startTicks) / Stopwatch.Frequency;
        }

        static long FutureTicks(double seconds)
        {
            return Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        }
        FileStream logStream;
        readonly byte[] logBuf = new byte[8192];
        readonly char[] logChars = new char[Encoding.UTF8.GetMaxCharCount(8192)];
        readonly Decoder logDecoder = Encoding.UTF8.GetDecoder();
        long logPos;
        string logRest = "";
        string runningConfig;
        ConfigInfo active;
        bool wireSockInstalled, installingWireSock, triedWireSock;
        EventWaitHandle showEvent;
        RegisteredWaitHandle showWaitReg;
        WebClient ipClient;

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

        // diagnostics tab (Testes & Saúde)
        CancellationTokenSource diagQuickCts, diagContCts;
        Thread diagContThread;
        int diagEpoch, diagContEpoch;
        bool diagQuickRunning, diagContinuousRunning, diagRepairRunning;
        long lastTimelineVersion = -1;
        string lastDiagSummary = "";
        string lastContSummary = "";
        string lastNetSignature = "";
        readonly object netSigLock = new object();
        long suppressMtuLogUntilTick;
        readonly Dictionary<string, long> netMonLogCooldown = new Dictionary<string, long>(StringComparer.Ordinal);
        NetDiag.MtuResult lastDiagMtu;
        NetDiag.ConflictReport lastDiagConflicts;
        bool lastDiagDnsFailed, lastDiagLogMtuErr;
        int lastDiagFixableCount;
        string pendingPostHealDiagNote;
        int mtuLogBurstCount;
        int bgHealInFlight, activeConnectWorkers;
        readonly object connectFlagLock = new object();
        bool bgMtuReducedThisSession;
        long mtuLogWindowStartTick, bgNetHealEnqueueTick, bgMtuHealEnqueueTick;

        T E<T>(string name) where T : class { return (T)w.FindName(name); }

        public MainWindow(bool startInTray)
        {
            w = (Window)XamlReader.Parse(Res.Text("MainWindow.xaml"));
            Chrome.Apply(w, 44);
            var frames = new IconBitmapDecoder(Res.Open("vpn.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            w.Icon = frames.First(f => f.PixelWidth == 32);
            titleIcon = E<Image>("TitleIcon");
            titleIcon.Source = frames.First(f => f.PixelWidth == 32);

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
            statusRing = E<Ellipse>("StatusRing");
            statusSpinner = E<Ellipse>("StatusSpinner");
            spinnerRotate = (RotateTransform)statusSpinner.RenderTransform;
            shieldBg = E<System.Windows.Shapes.Path>("ShieldBg");
            ravenBeak = E<System.Windows.Shapes.Path>("RavenBeak");
            ravenHead = E<System.Windows.Shapes.Path>("RavenHead");
            ravenBody = E<System.Windows.Shapes.Path>("RavenBody");
            ravenTail = E<System.Windows.Shapes.Path>("RavenTail");
            ravenEye = E<Ellipse>("RavenEye");
            E<Image>("SetupMedallion").Source = w.Icon;
            txtStatus = E<TextBlock>("TxtStatus");
            txtScope = E<TextBlock>("TxtScope");
            homeAppsPanel = E<WrapPanel>("HomeAppsPanel");
            txtUptime = E<TextBlock>("TxtUptime");
            txtDown = E<TextBlock>("TxtDown");
            txtDownIcon = E<TextBlock>("TxtDownIcon");
            txtUp = E<TextBlock>("TxtUp");
            trafficRow = E<FrameworkElement>("TrafficRow");
            trafficRow.ToolTipOpening += (s, e) =>
            {
                string tip = TrafficTip();
                if (tip == null) e.Handled = true;
                else trafficRow.ToolTip = tip;
            };
            txtFlag = E<TextBlock>("TxtFlag");
            txtFlagGlobe = E<TextBlock>("TxtFlagGlobe");
            txtSrvName = E<TextBlock>("TxtSrvName");
            txtSrvDetail = E<TextBlock>("TxtSrvDetail");
            txtSrvPing = E<TextBlock>("TxtSrvPing");
            txtIp = E<TextBlock>("TxtIp");
            txtLoc = E<TextBlock>("TxtLoc");
            txtOrg = E<TextBlock>("TxtOrg");
            txtIpNote = E<TextBlock>("TxtIpNote");
            glow = E<Ellipse>("Glow");
            serverList = E<StackPanel>("ServerList");
            emptyServers = E<StackPanel>("EmptyServers");
            txtServerSearch = E<TextBox>("TxtServerSearch");
            btnClearServerSearch = E<Button>("BtnClearServerSearch");
            serverSearchBox = E<FrameworkElement>("ServerSearchBox");
            btnFastest = E<Button>("BtnFastest");
            txtAppsCfg = E<TextBlock>("TxtAppsCfg");
            txtModeHint = E<TextBlock>("TxtModeHint");
            txtAppsSaved = E<TextBlock>("TxtAppsSaved");
            chkSharedApps = E<CheckBox>("ChkSharedApps");
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
            txtLockNote = E<TextBlock>("TxtLockNote");
            chkMtu = E<CheckBox>("ChkMtu");
            chkAutoHeal = E<CheckBox>("ChkAutoHeal");
            chkHotkey = E<CheckBox>("ChkHotkey");
            chkStartup = E<CheckBox>("ChkStartup");
            chkAuto = E<CheckBox>("ChkAuto");
            chkSkipUac = E<CheckBox>("ChkSkipUac");
            chkUpdates = E<CheckBox>("ChkUpdates");
            cardUpdate = E<Border>("CardUpdate");
            btnApplyUpdate = E<Button>("BtnApplyUpdate");
            btnCheckUpdate = E<Button>("BtnCheckUpdate");
            btnResetLock = E<Button>("BtnResetLock");
            txtUpdateFoundTitle = E<TextBlock>("TxtUpdateFoundTitle");
            txtUpdateFoundDetail = E<TextBlock>("TxtUpdateFoundDetail");
            txtUpdateBadge = E<TextBlock>("TxtUpdateBadge");
            panelColorPresets = E<WrapPanel>("PanelColorPresets");
            tabHome = E<RadioButton>("TabHome");
            tabServers = E<RadioButton>("TabServers");
            tabApps = E<RadioButton>("TabApps");
            tabDiag = E<RadioButton>("TabDiag");
            tabOptions = E<RadioButton>("TabOptions");
            pageHome = E<FrameworkElement>("PageHome");
            pageServers = E<FrameworkElement>("PageServers");
            pageApps = E<FrameworkElement>("PageApps");
            pageDiag = E<FrameworkElement>("PageDiag");
            pageOptions = E<FrameworkElement>("PageOptions");
            diagRouteBadge = E<Border>("DiagRouteBadge");
            txtDiagRouteBadge = E<TextBlock>("TxtDiagRouteBadge");
            cardDiagVerdict = E<Border>("CardDiagVerdict");
            diagVerdictDot = E<Ellipse>("DiagVerdictDot");
            txtDiagVerdict = E<TextBlock>("TxtDiagVerdict");
            txtDiagVerdictSub = E<TextBlock>("TxtDiagVerdictSub");
            btnDiagQuick = E<Button>("BtnDiagQuick");
            btnDiagContinuous = E<Button>("BtnDiagContinuous");
            btnDiagRepair = E<Button>("BtnDiagRepair");
            btnDiagCopy = E<Button>("BtnDiagCopy");
            cardDiagLive = E<Border>("CardDiagLive");
            txtDiagLiveStatus = E<TextBlock>("TxtDiagLiveStatus");
            txtDiagLiveTime = E<TextBlock>("TxtDiagLiveTime");
            txtDiagSentLoss = E<TextBlock>("TxtDiagSentLoss");
            txtDiagPing = E<TextBlock>("TxtDiagPing");
            txtDiagJitter = E<TextBlock>("TxtDiagJitter");
            txtDiagDrops = E<TextBlock>("TxtDiagDrops");
            txtDiagGw = E<TextBlock>("TxtDiagGw");
            txtDiagEp = E<TextBlock>("TxtDiagEp");
            chkItem1Dot = E<Ellipse>("ChkItem1Dot");
            chkItem2Dot = E<Ellipse>("ChkItem2Dot");
            chkItem3Dot = E<Ellipse>("ChkItem3Dot");
            chkItem4Dot = E<Ellipse>("ChkItem4Dot");
            chkItem5Dot = E<Ellipse>("ChkItem5Dot");
            chkItem6Dot = E<Ellipse>("ChkItem6Dot");
            chkItem7Dot = E<Ellipse>("ChkItem7Dot");
            txtChk1Detail = E<TextBlock>("TxtChk1Detail");
            txtChk2Detail = E<TextBlock>("TxtChk2Detail");
            txtChk3Detail = E<TextBlock>("TxtChk3Detail");
            txtChk4Detail = E<TextBlock>("TxtChk4Detail");
            txtChk5Detail = E<TextBlock>("TxtChk5Detail");
            txtChk6Detail = E<TextBlock>("TxtChk6Detail");
            txtChk7Detail = E<TextBlock>("TxtChk7Detail");
            btnDiagApps = E<Button>("BtnDiagApps");
            btnDiagClearLog = E<Button>("BtnDiagClearLog");
            txtDiagTimelineEmpty = E<TextBlock>("TxtDiagTimelineEmpty");
            diagTimelineList = E<StackPanel>("DiagTimelineList");

            // title bar
            var btnMin = E<Button>("BtnMin");
            var btnClose = E<Button>("BtnClose");
            WindowChrome.SetIsHitTestVisibleInChrome(btnMin, true);
            WindowChrome.SetIsHitTestVisibleInChrome(btnClose, true);
            btnMin.Click += (s, e) => w.WindowState = WindowState.Minimized;
            btnClose.Click += (s, e) => w.Close();
            w.Closing += (s, e) =>
            {
                SaveWindowPosition();
                if (exiting) return;
                e.Cancel = true;
                w.Hide();
                if (!hintShown)
                {
                    tray.ShowBalloonTip(2000, "VPN Panel", "Continuo aqui na bandeja. Clique com o botão direito no ícone para sair.", WF.ToolTipIcon.Info);
                    hintShown = true;
                }
            };
            w.LocationChanged += (s, e) =>
            {
                if (w.IsVisible && w.WindowState == WindowState.Normal)
                {
                    settings.WindowLeft = w.Left;
                    settings.WindowTop = w.Top;
                }
            };
            w.SourceInitialized += (s, e) =>
            {
                windowHandle = new WindowInteropHelper(w).Handle;
                msgTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
                var src = HwndSource.FromHwnd(windowHandle);
                if (src != null) src.AddHook(WndProc);
                SyncHotkey();
            };
            w.IsVisibleChanged += (s, e) => OnVisibilityChanged();
            w.StateChanged += (s, e) => OnVisibilityChanged();
            Application.Current.SessionEnding += (s, e) => Quit();

            // tabs
            tabHome.Checked += (s, e) => ShowPage(HomePage);
            tabServers.Checked += (s, e) => { ShowPage(pageServers); MeasureLatency(false); };
            tabApps.Checked += (s, e) => ShowPage(pageApps);
            tabDiag.Checked += (s, e) => ShowPage(pageDiag);
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
            txtScope.MouseLeftButtonUp += (s, e) =>
            {
                if (shown == Conn.On && active != null)
                    Dialogs.VerifyRouting(w, active, Tunnel.Ipv6Isolated, AppIcon, appsNeedReconnect);
            };
            E<Button>("BtnServerCard").Click += (s, e) => tabServers.IsChecked = true;
            E<Button>("BtnRefreshIp").Click += (s, e) => CheckIp();
            E<Button>("BtnRetry").Click += (s, e) => { Action a = bannerPrimary; HideBanner(); if (a != null) a(); };
            E<Button>("BtnErrLog").Click += (s, e) => { if (bannerSecondary != null) bannerSecondary(); };
            E<Button>("BtnErrClose").Click += (s, e) => HideBanner();

            // Servidores
            E<Button>("BtnImport").Click += (s, e) => BrowseImport();
            E<Button>("BtnEmptyHelp").Click += (s, e) => OpenGuide(false);
            E<Button>("BtnServersHelp").Click += (s, e) => OpenGuide(false);
            E<Button>("BtnPing").Click += (s, e) => MeasureLatency(true);
            btnFastest.Click += (s, e) => ConnectFastest();
            txtServerSearch.TextChanged += (s, e) =>
            {
                btnClearServerSearch.Visibility = string.IsNullOrEmpty(txtServerSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
                FilterServerList();
            };
            btnClearServerSearch.Click += (s, e) => { txtServerSearch.Text = ""; txtServerSearch.Focus(); };
            Latency.Measured += OnLatencyMeasured;
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
            E<Button>("BtnVerifyApps").Click += (s, e) =>
            {
                if (active != null)
                {
                    var preview = new ConfigInfo { Name = active.Name, Mode = editMode, Apps = new List<string>(editApps), Readable = true };
                    Dialogs.VerifyRouting(w, preview, Tunnel.Ipv6Isolated || editMode != AppMode.All, AppIcon, appsNeedReconnect);
                }
            };
            E<Button>("BtnApplyNow").Click += (s, e) => Reconnect();

            // Testes & Saúde
            btnDiagQuick.Click += (s, e) => RunQuickDiag();
            btnDiagContinuous.Click += (s, e) => ToggleContinuousDiag();
            btnDiagRepair.Click += (s, e) => RepairDiagNetwork();
            btnDiagCopy.Click += (s, e) => CopyDiagReport();
            btnDiagClearLog.Click += (s, e) => { NetMon.Clear(); UpdateDiagTimeline(); };
            btnDiagApps.Click += (s, e) =>
            {
                if (active == null) { tabApps.IsChecked = true; return; }
                AppMode eff = EffectiveAppMode();
                var preview = new ConfigInfo
                {
                    Name = active.Name,
                    Mode = eff,
                    Apps = settings.SharedApps ? new List<string>(settings.Apps) : new List<string>(active.Apps),
                    Readable = active.Readable
                };
                Dialogs.VerifyRouting(w, preview, Tunnel.Ipv6Isolated || eff != AppMode.All, AppIcon, appsNeedReconnect);
            };

            // Opções
            loading = true;
            chkReconnect.IsChecked = settings.AutoReconnect;
            chkLock.IsChecked = settings.NetworkLock;
            chkMtu.IsChecked = settings.OptimizeMtu;
            chkAutoHeal.IsChecked = settings.AutoHeal;
            chkAuto.IsChecked = settings.AutoConnect;
            chkSkipUac.IsChecked = settings.SkipUac;
            chkHotkey.IsChecked = settings.Hotkey;
            chkUpdates.IsChecked = settings.CheckUpdates;
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
            UpdateLockAvailability();
            onToggle(chkMtu, () =>
            {
                SaveSettings();
                if (active != null && active.Readable && (active.Mtu == 0 || active.AutoMtu))
                {
                    bool updateMtu = (settings.OptimizeMtu && active.Mtu == 0) || (!settings.OptimizeMtu && active.AutoMtu);
                    if (updateMtu)
                    {
                        try
                        {
                            ConfFile.Rewrite(Paths.Conf(active.Name), false, AppMode.All, null, true, settings.OptimizeMtu ? 1380 : 0);
                            active = ConfigInfo.Read(active.Name);
                        }
                        catch { }
                    }
                }
            });
            onToggle(chkAutoHeal, SaveSettings);
            onToggle(chkAuto, SaveSettings);
            onToggle(chkSkipUac, () => { SaveSettings(); SyncTasks(); });
            onToggle(chkHotkey, () => { SaveSettings(); SyncHotkey(); });
            onToggle(chkUpdates, SaveSettings);
            onToggle(chkStartup, ToggleStartup);
            onToggle(chkSharedApps, ToggleSharedApps);
            btnCheckUpdate.Click += (s, e) => CheckForUpdates(true);
            btnApplyUpdate.Click += (s, e) => InstallUpdate();
            btnResetLock.Click += (s, e) => ResetNetworkLockAction();
            E<Button>("BtnBackup").Click += (s, e) => ExportBackup();
            E<Button>("BtnLog").Click += (s, e) => OpenLog();
            E<Button>("BtnDataDir").Click += (s, e) => Process.Start("explorer.exe", "\"" + Paths.Data + "\"");
            E<Button>("BtnQuit").Click += (s, e) => Quit();

            // theme & tray
            InitColorSwatches();
            ApplyAccentColor(settings.AccentColor ?? "green", false);
            var renderer = new DarkMenuRenderer();
            var menu = trayMenu = new WF.ContextMenuStrip { Renderer = renderer, ShowImageMargin = false, ShowCheckMargin = true, Font = new SD.Font("Segoe UI", 9.5F) };
            miStatus = (WF.ToolStripMenuItem)menu.Items.Add("Desconectado");
            miStatus.Enabled = false;
            menu.Items.Add(new WF.ToolStripSeparator());
            miToggle = (WF.ToolStripMenuItem)menu.Items.Add("Conectar", null, (s, e) => Toggle());
            menu.Items.Add("Conectar ao mais rápido", null, (s, e) => ConnectFastest());
            menu.Items.Add(new WF.ToolStripSeparator());
            // recent servers go here (UpdateTrayServers), then every server in a submenu
            miAllServers = (WF.ToolStripMenuItem)menu.Items.Add("Todos os servidores");
            var allDrop = (WF.ToolStripDropDownMenu)miAllServers.DropDown;
            allDrop.Renderer = renderer; // submenus don't inherit the parent's renderer
            allDrop.ShowImageMargin = false;
            allDrop.ShowCheckMargin = true;
            miServersEnd = new WF.ToolStripSeparator();
            menu.Items.Add(miServersEnd);
            menu.Items.Add("Liberar internet (reset do bloqueio)", null, (s, e) => ResetNetworkLockAction());
            menu.Items.Add("Abrir VPN Panel", null, (s, e) => ShowWindow());
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Sair (a VPN continua como está)", null, (s, e) => Quit());
            foreach (WF.ToolStripItem item in menu.Items) item.Padding = TrayPadding;
            // built when the menu opens, never in the background
            menu.Opening += (s, e) => UpdateTrayServers();
            tray = new WF.NotifyIcon { Icon = trayOff, Text = "VPN Panel", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };
            tray.BalloonTipClicked += (s, e) =>
            {
                ShowWindow();
                if (availableUpdate != null && availableUpdate.IsNewer)
                {
                    pendingUpdatePrompt = false;
                    PromptUpdate(availableUpdate, true);
                }
            };

            wireSockInstalled = Paths.WireSock != null;
            UpdateAbout();

            geoDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            geoDebounce.Tick += (s, e) => { geoDebounce.Stop(); RefreshConfigs(); };

            RefreshConfigs();
            MigrateSharedApps();
            LoadAppsEditor();
            UpdateSetup();

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            pollTimer.Tick += (s, e) => Poll();
            // one-shot IP check a few seconds after connecting/disconnecting
            ipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            ipTimer.Tick += (s, e) => { ipTimer.Stop(); CheckIp(); };
            reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            reconnectTimer.Tick += (s, e) => { reconnectTimer.Stop(); Connect(true); };
            updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            updateCheckTimer.Tick += (s, e) =>
            {
                updateCheckTimer.Interval = TimeSpan.FromMinutes(3);
                if (settings.CheckUpdates && !updateDialogOpen)
                {
                    CheckForUpdates(false);
                }
            };
            updateCheckTimer.Start();

            fastestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            fastestTimer.Tick += (s, e) => { fastestTimer.Stop(); FinishConnectFastest(); };

            powerModeHandler = (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend || e.Mode == Microsoft.Win32.PowerModes.Resume)
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null)
                    {
                        bool isResume = e.Mode == Microsoft.Win32.PowerModes.Resume;
                        ui.BeginInvoke(new Action(() =>
                        {
                            if (exiting) return;
                            // Reset monotonic handshake/connect timers so sleep duration never counts toward timeouts
                            if (pendingSinceTick != null) pendingSinceTick = NowTicks();
                            if (rehandshakeSuspectTick > 0) { rehandshakeSuspectTick = 0; rehandshakeGen++; }
                            rehandshakeConfirmedDown = false;
                            if (connectLaunchTick > 0) connectLaunchTick = NowTicks();
                            if (isResume)
                            {
                                resumeGraceUntilTick = FutureTicks(18.0);
                                Latency.RouteChanged();
                                ipTimer.Stop();
                                ipTimer.Start();
                            }
                        }));
                    }
                }
            };
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += powerModeHandler; } catch { }

            networkAddressHandler = (s, e) =>
            {
                ThreadPool.QueueUserWorkItem(_ => RecordNetworkAddressDelta());
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        Latency.RouteChanged();
                        ipTimer.Stop();
                        ipTimer.Start();
                    }));
                }
            };
            networkAvailHandler = (s, e) =>
            {
                NetMon.Record("Rede", e.IsAvailable ? NetMon.EventKind.Info : NetMon.EventKind.Error,
                    e.IsAvailable ? "Rede do Windows ficou disponível" : "Rede do Windows ficou indisponível (sem link)");
            };
            try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += networkAddressHandler; } catch { }
            try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += networkAvailHandler; } catch { }
            ThreadPool.QueueUserWorkItem(_ => RecordNetworkAddressDelta(true));

            ListenForShowRequests();
            WatchConfigs();
            WatchDownloads();
            SyncTasks();
            RestoreWindowPosition();
            if (!startInTray) w.Show();
            w.Dispatcher.BeginInvoke(new Action(OnStart));
        }

        void RestoreWindowPosition()
        {
            if (double.IsNaN(settings.WindowLeft) || double.IsNaN(settings.WindowTop)) return;
            try
            {
                var rect = new SD.Rectangle((int)settings.WindowLeft + 40, (int)settings.WindowTop + 40, 200, 200);
                if (WF.Screen.AllScreens.Any(scr => scr.WorkingArea.IntersectsWith(rect)))
                {
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = settings.WindowLeft;
                    w.Top = settings.WindowTop;
                }
            }
            catch { }
        }

        void SaveWindowPosition()
        {
            if (w == null) return;
            if (w.WindowState == WindowState.Normal && !double.IsNaN(w.Left) && !double.IsNaN(w.Top))
            {
                settings.WindowLeft = w.Left;
                settings.WindowTop = w.Top;
                settings.Save();
            }
        }

        bool WindowShowing { get { return w != null && w.IsVisible && w.WindowState != WindowState.Minimized; } }

        void OnVisibilityChanged()
        {
            UpdateSpinnerAnimation();
            if (!WindowShowing) Latency.CancelQueued();
            if (WindowShowing)
            {
                if (serversDirty) RenderServerList();
                if (appsDirty) RenderApps();
                RenderHomeApps();
                UpdateTraffic();
                if (wasRunning == true) UpdateUptimeText();
                if (ipDirty) CheckIp();
                MeasureLatency(false);
                ApplyPowerButtonVisuals(shown, false);
                if (pendingUpdatePrompt && availableUpdate != null && availableUpdate.IsNewer)
                {
                    pendingUpdatePrompt = false;
                    Updater.ReleaseInfo info = availableUpdate;
                    w.Dispatcher.BeginInvoke(new Action(() => PromptUpdate(info, false)));
                }
                else if (settings.CheckUpdates && !updateDialogOpen && (DateTime.UtcNow - lastUpdateCheck).TotalSeconds >= 45)
                {
                    CheckForUpdates(false);
                }
            }
            else
            {
                if (glow.Visibility == Visibility.Visible)
                {
                    glow.BeginAnimation(UIElement.OpacityProperty, null);
                    glow.Visibility = Visibility.Collapsed;
                }
            }
            UpdatePollInterval();
        }

        void UpdatePollInterval()
        {
            if (pollTimer == null) return;
            bool fast = connecting || reconnectPending || WindowShowing || (wasRunning == true && monitoring && (!handshakeOk || pendingSinceTick != null));
            TimeSpan wanted = TimeSpan.FromSeconds(fast ? 1 : (wasRunning == true ? 3 : 12));
            if (pollTimer.Interval != wanted) pollTimer.Interval = wanted;
        }

        void OnStart()
        {
            Poll();
            pollTimer.Start();
            // WireSock is required: install it as soon as someone is looking (not on a silent tray start)
            if (w.IsVisible && !wireSockInstalled) InstallWireSock();
            if (wasRunning == true)
            {
                // Defer split-tunneling IPv6 isolation / ghost route check until async Tunnel.CommandLine + NoteCommandLine finish
                startupIsolationCheckPending = true;
                if (!cmdLineLookupRunning) CheckStartupIsolationAsync();
                CheckIp();
            }
            else if (wasRunning == false)
            {
                if (settings.AutoConnect && Ready)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (Tunnel.HasPendingNetworkLock || settings.NetworkLock) Tunnel.ResetNetworkLock();
                        else ConfFile.CleanGhostNetwork();
                        var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                        if (ui != null) ui.BeginInvoke(new Action(() => { if (!exiting && Ready) Connect(); }));
                    });
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (Tunnel.HasPendingNetworkLock || settings.NetworkLock) Tunnel.ResetNetworkLock();
                        else ConfFile.CleanGhostNetwork();
                    });
                    CheckIp();
                }
            }
            else CheckIp();
        }

        void CheckStartupIsolationAsync()
        {
            startupIsolationCheckPending = false;
            if (exiting || wasRunning != true || active == null || EffectiveAppMode() == AppMode.All) return;
            bool isolated = Tunnel.Ipv6Isolated;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ghost = ConfFile.HasGhostIpv6();
                if (!isolated || ghost)
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null) ui.BeginInvoke(new Action(() =>
                    {
                        if (!exiting && wasRunning == true && !userStopped && !reconnectPending && active != null && EffectiveAppMode() != AppMode.All)
                            Reconnect();
                    }));
                }
            });
        }

        // A second launch of the exe signals this event instead of opening another window
        void ListenForShowRequests()
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            showWaitReg = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (state, timedOut) => w.Dispatcher.BeginInvoke(new Action(ShowWindow)),
                null, -1, false);
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
                ConfigInfo.InvalidateAll();
                RefreshConfigs();
                LoadAppsEditor();
                UpdateSetup();
            };
            configWatcher = new FileSystemWatcher(Paths.Configs, "*.conf") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (s, e) => w.Dispatcher.BeginInvoke(new Action(() =>
            {
                // WriteApps (e.g. Connect applying the shared apps) already updated the cache: no full reload
                if (e != null && ConfFile.IsOwnWrite(System.IO.Path.GetFileNameWithoutExtension(e.Name)) && File.Exists(e.FullPath)) return;
                configDebounce.Stop();
                configDebounce.Start();
            }));
            configWatcher.Created += changed;
            configWatcher.Deleted += changed;
            configWatcher.Changed += changed;
            configWatcher.Renamed += (s, e) => changed(s, e);
            configWatcher.Error += (s, e) => changed(s, null);
            configWatcher.EnableRaisingEvents = true;
        }

        bool startupTouched;

        // Keeps the scheduled tasks pointing at this exe (in the background: COM calls take a moment)
        void SyncTasks()
        {
            bool skip = settings.SkipUac;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool logonExists = Tasks.Sync(skip);
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (startupTouched) return;
                    loading = true;
                    chkStartup.IsChecked = logonExists;
                    loading = false;
                }));
            });
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                Toggle();
                handled = true;
            }
            else if (msgTaskbarCreated != 0 && msg == msgTaskbarCreated)
            {
                tray.Visible = false;
                tray.Visible = true;
            }
            return IntPtr.Zero;
        }

        void SyncHotkey()
        {
            if (windowHandle == IntPtr.Zero) return;
            if (settings.Hotkey && !hotkeyRegistered)
            {
                hotkeyRegistered = RegisterHotKey(windowHandle, HotkeyId, ModControl | ModAlt, VkV);
                if (!hotkeyRegistered)
                {
                    Dialogs.Info(w, "Atalho não registrado", "O atalho Ctrl + Alt + V já está sendo usado por outro aplicativo.");
                    chkHotkey.IsChecked = false;
                    settings.Hotkey = false;
                    settings.Save();
                }
            }
            else if (!settings.Hotkey && hotkeyRegistered)
            {
                UnregisterHotKey(windowHandle, HotkeyId);
                hotkeyRegistered = false;
            }
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
            Paths.SearchingFast = true;
            step1Status.Text = "Instalando o WireSock...";
            step1Working.Visibility = Visibility.Visible;
            step1Failed.Visibility = Visibility.Collapsed;
            UpdateSpinnerAnimation();
            new Thread(() =>
            {
                string error = WireSockSetup.Install(text => w.Dispatcher.BeginInvoke(new Action(() => step1Status.Text = text)));
                w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    installingWireSock = false;
                    Paths.SearchingFast = false;
                    UpdateSpinnerAnimation();
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
            CancelAllDiag();
            SaveWindowPosition();
            if (powerModeHandler != null) { try { Microsoft.Win32.SystemEvents.PowerModeChanged -= powerModeHandler; } catch { } }
            if (networkAddressHandler != null) { try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= networkAddressHandler; } catch { } }
            if (networkAvailHandler != null) { try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= networkAvailHandler; } catch { } }
            if (hotkeyRegistered && windowHandle != IntPtr.Zero)
            {
                UnregisterHotKey(windowHandle, HotkeyId);
                hotkeyRegistered = false;
            }
            pollTimer.Stop();
            if (updateCheckTimer != null) updateCheckTimer.Stop();
            CloseLogStream();
            Geo.Flush();
            if (showWaitReg != null) { try { showWaitReg.Unregister(null); } catch { } }
            if (showEvent != null) { try { showEvent.Dispose(); } catch { } }
            tray.Visible = false;
            tray.Dispose();
            if (trayOn != null) trayOn.Dispose();
            if (trayOff != null) trayOff.Dispose();
            if (trayWarn != null) trayWarn.Dispose();
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

                            status.Foreground = Dialogs.B("Accent");
                            status.Text = "Instalando atualização...";
                            btnNow.Content = "Instalando...";
                            txtUpdateFoundDetail.Text = "Instalando atualização...";
                            btnApplyUpdate.Content = "Instalando...";

                            string targetDigest = !string.IsNullOrEmpty(info.SetupDigest) ? info.SetupDigest : info.AppDigest;
                            if (!string.IsNullOrEmpty(targetDigest))
                            {
                                settings.InstalledDigest = targetDigest;
                                settings.Save();
                            }

                            bool installed = Paths.ExeDir.Equals(Installer.Dir, StringComparison.OrdinalIgnoreCase);
                            var psi = new ProcessStartInfo(tempFile, installed ? "--silent" : "") { UseShellExecute = true };
                            Process.Start(psi);
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

        void SaveSettings()
        {
            settings.Config = active != null ? active.Name : null;
            settings.AutoReconnect = chkReconnect.IsChecked == true;
            if (chkLock.IsEnabled) settings.NetworkLock = chkLock.IsChecked == true;
            settings.OptimizeMtu = chkMtu.IsChecked == true;
            if (!settings.OptimizeMtu) settings.AdaptiveMtu = 0;
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

        // What the user sees. "Connected" needs a WireGuard handshake, not just a running client.
        enum Conn { Off, Connecting, Retrying, On, NoResponse }

        const int WarnAfter = 15, GiveUpAfter = 60; // seconds without a handshake response

        Conn State
        {
            get
            {
                if (reconnectPending) return Conn.Connecting;
                // being stopped: the process lingers until the next poll
                if (wasRunning == true && userStopped) return Conn.Off;
                if (wasRunning == true)
                {
                    if (!monitoring) return Conn.On; // started outside the app: no log to check
                    if (!handshakeOk && pendingSinceTick != null && ElapsedSeconds(pendingSinceTick.Value) > WarnAfter) return Conn.NoResponse;
                    if (rehandshakeConfirmedDown) return Conn.NoResponse;
                    return handshakeOk ? Conn.On : Conn.Connecting;
                }
                if (reconnectTimer != null && reconnectTimer.IsEnabled) return Conn.Retrying;
                return connecting ? Conn.Connecting : Conn.Off;
            }
        }

        void QueuePostDropCleanup()
        {
            int dropSeq = ++postDropCleanupSeq;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ConfFile.DeleteRuntimeCopies();
                if (exiting || dropSeq != postDropCleanupSeq || connecting || reconnectPending || wasRunning == true) return;
                if (Tunnel.HasPendingNetworkLock || settings.NetworkLock) Tunnel.ResetNetworkLock();
                else ConfFile.CleanGhostNetwork();
            });
        }

        void Toggle()
        {
            if (reconnectTimer.IsEnabled)
            {
                reconnectTimer.Stop();
                reconnectAttempts = 0;
                bool needCleanup = pendingPreConnectCleanup;
                pendingPreConnectCleanup = false;
                NetMon.Record("Túnel", NetMon.EventKind.Info, "Reconexão automática cancelada pelo usuário");
                UpdateState();
                UpdatePollInterval();
                if (needCleanup) QueuePostDropCleanup();
                return;
            }
            if ((wasRunning == true && !userStopped) || connecting || reconnectPending) Disconnect(true);
            else Connect();
        }

        void Connect() { Connect(false); }

        void Connect(bool fromAutoReconnect)
        {
            HideBanner();
            if (!Ready)
            {
                ShowWindow();
                tabHome.IsChecked = true;
                ShowPage(HomePage);
                return;
            }
            if (diagRepairRunning)
            {
                if (fromAutoReconnect)
                {
                    if (!userStopped && wasRunning != true && !reconnectPending && reconnectTimer != null && !reconnectTimer.IsEnabled)
                        reconnectTimer.Start();
                }
                else if (WindowShowing)
                {
                    Dialogs.Info(w, "Reparo em andamento", "Aguarde a conclusão da correção na aba Testes antes de conectar.");
                }
                UpdateState();
                return;
            }
            if (reconnectTimer != null && reconnectTimer.IsEnabled) reconnectTimer.Stop();
            reconnectPending = false;
            if (connecting || (wasRunning == true && !userStopped))
            {
                UpdateState();
                return;
            }
            StopContinuousDiag("Interrompido: túnel alterado");

            string cfgName = active.Name;
            string wireSockExe = Paths.WireSock;
            AppMode targetMode = settings.AppsMode != AppMode.All && settings.Apps.Count == 0 ? AppMode.All : settings.AppsMode;
            AppMode effectiveMode = settings.SharedApps ? targetMode : active.Mode;
            bool netLock = settings.NetworkLock && effectiveMode == AppMode.All;
            bool updateApps = settings.SharedApps && !SameApps(active, settings.AppsMode, settings.Apps);
            bool updateMtu = (settings.OptimizeMtu && active.Mtu == 0) || (!settings.OptimizeMtu && active.AutoMtu);
            List<string> targetApps = targetMode == AppMode.All ? new List<string>() : new List<string>(settings.Apps);
            int targetMtu = settings.OptimizeMtu ? 1380 : 0;

            if (!fromAutoReconnect) reconnectAttempts = 0;
            bool needPreCleanup = pendingPreConnectCleanup;
            pendingPreConnectCleanup = false;
            postDropCleanupSeq++;
            bgMtuReducedThisSession = false;
            mtuLogBurstCount = 0;
            mtuLogWindowStartTick = 0;
            CloseLogStream();
            Traffic.Stop();
            ResetTrafficLabels();
            userStopped = false;
            connecting = true;
            connectPausedForDialog = false;
            connectLaunchTick = 0;
            connectTotalStartTick = NowTicks();
            StartMonitoring(true);
            appsNeedReconnect = false;
            NetMon.Record("Túnel", NetMon.EventKind.Info, (fromAutoReconnect ? "Reconectando " : "Conectando ") + cfgName + " (" + effectiveMode + ")");
            settings.AddRecent(cfgName);
            UpdateAppsSaved();
            UpdateState();
            UpdatePollInterval();

            bool autoHeal = settings.AutoHeal;
            bool optMtuSnap = settings.OptimizeMtu;
            int maxMtuSnap = EffectiveMaxMtu();
            int seq = ++connectSeq;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool markedConnecting = false;
                try
                {
                    if (needPreCleanup)
                    {
                        if (Tunnel.HasPendingNetworkLock || settings.NetworkLock) Tunnel.ResetNetworkLock();
                        else ConfFile.CleanGhostNetwork();
                        if (seq != connectSeq) return;
                    }
                    if (autoHeal)
                    {
                        AutoHealer.TryBackgroundAutoHeal(autoHeal, optMtuSnap, maxMtuSnap, false, false, false, null, () => exiting || seq != connectSeq);
                        if (seq != connectSeq) return;
                    }
                    lock (connectFlagLock)
                    {
                        if (seq != connectSeq) return;
                        activeConnectWorkers++;
                        Tunnel.Connecting = true;
                        markedConnecting = true;
                    }
                    if (Paths.WireSockServiceRunning)
                    {
                        bool confirmed = false;
                        Exception stopEx = null;
                        connectPausedForDialog = true;
                        try
                        {
                            w.Dispatcher.Invoke(new Action(() =>
                            {
                                if (seq != connectSeq) return;
                                ShowWindow();
                                confirmed = Dialogs.Confirm(w, "O WireSock já está conectado",
                                    "O aplicativo do WireSock está com uma conexão ativa. Para conectar pelo VPN Panel, essa conexão precisa ser desligada.",
                                    "Desligar e conectar", false);
                            }));
                        }
                        finally { connectPausedForDialog = false; }
                        if (seq != connectSeq) return;
                        if (!confirmed)
                        {
                            w.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (seq != connectSeq) return;
                                connecting = false;
                                connectLaunchTick = 0;
                                StopMonitoring();
                                UpdateState();
                                UpdatePollInterval();
                            }));
                            return;
                        }
                        try { Paths.StopWireSockService(); }
                        catch (Exception ex) { stopEx = ex; }
                        if (stopEx != null)
                        {
                            w.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (seq != connectSeq) return;
                                connecting = false;
                                connectLaunchTick = 0;
                                StopMonitoring();
                                UpdateState();
                                UpdatePollInterval();
                                Dialogs.Info(w, "Não foi possível desligar", "Desconecte pelo aplicativo do WireSock e tente de novo.\n\n" + stopEx.Message);
                            }));
                            return;
                        }
                    }

                    if (seq != connectSeq) return;
                    if (updateApps || updateMtu)
                    {
                        try
                        {
                            ConfFile.Rewrite(Paths.Conf(cfgName), updateApps, targetMode, targetApps, updateMtu, targetMtu);
                            ConfigInfo updated = ConfigInfo.Read(cfgName);
                            w.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (active != null && active.Name.Equals(cfgName, StringComparison.OrdinalIgnoreCase))
                                    active = updated;
                            }));
                        }
                        catch (Exception ex)
                        {
                            if (updateApps)
                            {
                                Exception appEx = ex;
                                w.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (seq != connectSeq) return;
                                    connecting = false;
                                    connectLaunchTick = 0;
                                    StopMonitoring();
                                    UpdateState();
                                    UpdatePollInterval();
                                    Dialogs.Info(w, "Não foi possível aplicar os apps", appEx.Message);
                                }));
                                return;
                            }
                        }
                    }

                    if (seq != connectSeq) return;
                    try
                    {
                        Tunnel.Start(wireSockExe, cfgName, netLock, maxMtuSnap);
                        w.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (seq != connectSeq) return;
                            SyncMtuUiFromSettings();
                            settings.Save();
                            connectLaunchTick = NowTicks();
                            Poll();
                        }));
                    }
                    catch (Exception ex)
                    {
                        Exception startEx = ex;
                        w.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (seq != connectSeq) return;
                            connecting = false;
                            connectLaunchTick = 0;
                            StopMonitoring();
                            UpdateState();
                            UpdatePollInterval();
                            ShowError("Não foi possível iniciar o WireSock", startEx.Message);
                        }));
                    }
                }
                finally
                {
                    if (markedConnecting)
                    {
                        lock (connectFlagLock)
                        {
                            if (--activeConnectWorkers == 0)
                                Tunnel.Connecting = false;
                        }
                    }
                }
            });
        }

        // byUser: the user asked for it (resets the auto-reconnect budget)
        void Disconnect(bool byUser)
        {
            StopContinuousDiag("Interrompido: túnel alterado");
            int seq = ++connectSeq;
            postDropCleanupSeq++;
            userStopped = true;
            connecting = false;
            connectPausedForDialog = false;
            connectLaunchTick = 0;
            wasRunning = false;
            runningConfig = null;
            StopMonitoring();
            Traffic.Stop();
            ResetTrafficLabels();
            NetMon.Record("Túnel", NetMon.EventKind.Info, byUser ? "Desconectado pelo usuário" : "Desconectando túnel");
            if (!reconnectPending) pendingPostHealDiagNote = null;
            if (byUser)
            {
                pendingPostHealDiagNote = null;
                reconnectPending = false;
                pendingPreConnectCleanup = false;
                reconnectAttempts = 0;
                if (reconnectTimer != null && reconnectTimer.IsEnabled) reconnectTimer.Stop();
            }
            bool netLock = settings.NetworkLock;
            UpdateState();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tunnel.Stop(netLock);
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        ipTimer.Stop();
                        ipTimer.Start();
                        UpdatePollInterval();
                        if (seq != connectSeq) return;
                        if (reconnectPending)
                        {
                            reconnectPending = false;
                            Connect(true);
                        }
                        else
                        {
                            Poll();
                        }
                    }));
                }
            });
            UpdatePollInterval();
        }

        void Reconnect()
        {
            if (wasRunning != true && !connecting) { Connect(); return; }
            NetMon.Record("Túnel", NetMon.EventKind.Warn, "Reiniciando túnel...");
            reconnectPending = true;
            Disconnect(false);
        }

        void CloseLogStream()
        {
            if (logStream == null) return;
            try { logStream.Dispose(); } catch { }
            logStream = null;
        }

        void StopMonitoring()
        {
            monitoring = false;
            rehandshakeSuspectTick = 0;
            rehandshakeGen++;
            rehandshakeConfirmedDown = false;
            rehandshakeOnlyChecked10s = false;
            activeProbeRunning = false;
            CloseLogStream();
        }

        // Follows the WireSock log (written at -log-level info) for handshake events
        void StartMonitoring(bool fresh)
        {
            CloseLogStream();
            monitoring = true;
            handshakeOk = false;
            sawHandshake = false;
            seekToTail = !fresh;
            pendingSinceTick = fresh ? (long?)NowTicks() : null;
            rehandshakeSuspectTick = 0;
            rehandshakeGen++;
            rehandshakeConfirmedDown = false;
            rehandshakeOnlyChecked10s = false;
            activeProbeRunning = false;
            mtuLogBurstCount = 0;
            mtuLogWindowStartTick = 0;
            logPos = 0;
            logRest = "";
            logDecoder.Reset();
        }

        static string ExtractLogMessage(string line, out string level)
        {
            level = "";
            if (string.IsNullOrEmpty(line)) return "";
            Match lm = Regex.Match(line, "\"log_level\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (lm.Success) level = lm.Groups[1].Value.Trim().ToLowerInvariant();
            Match mm = Regex.Match(line, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (mm.Success)
            {
                string msg = mm.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                return Regex.Replace(msg, @"\[\w+\]:\s*(Unhandled exception\s*)?", "").Trim();
            }
            return Regex.Replace(line.Trim(), @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s*", "").Trim();
        }

        void QueueBackgroundAutoHeal(bool tunnelRunning, bool repeatedMtuError)
        {
            if (exiting || !settings.AutoHeal) return;
            if (repeatedMtuError)
            {
                if (!settings.OptimizeMtu || bgMtuReducedThisSession) return;
                if (bgMtuHealEnqueueTick > 0 && ElapsedSeconds(bgMtuHealEnqueueTick) < 60.0) return;
            }
            else
            {
                if (bgNetHealEnqueueTick > 0 && ElapsedSeconds(bgNetHealEnqueueTick) < 60.0) return;
            }
            if (Interlocked.CompareExchange(ref bgHealInFlight, 1, 0) != 0) return;

            if (repeatedMtuError) bgMtuHealEnqueueTick = NowTicks();
            else bgNetHealEnqueueTick = NowTicks();

            bool autoHealSnap = settings.AutoHeal;
            bool optMtuSnap = settings.OptimizeMtu;
            int curMaxMtuSnap = DiagEffectiveMtu(tunnelRunning, EffectiveAppMode(), settings);
            bool allowMtu = repeatedMtuError && !bgMtuReducedThisSession;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AutoHealer.FixReport hr = null;
                try
                {
                    hr = AutoHealer.TryBackgroundAutoHeal(
                        autoHealSnap,
                        optMtuSnap,
                        curMaxMtuSnap,
                        tunnelRunning,
                        repeatedMtuError,
                        allowMtu,
                        null,
                        () => exiting || Tunnel.Connecting || reconnectPending || (!tunnelRunning && wasRunning == true));
                }
                finally
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null)
                    {
                        ui.BeginInvoke(new Action(() =>
                        {
                            Interlocked.Exchange(ref bgHealInFlight, 0);
                            if (exiting) return;
                            ApplyHealReportToSettings(hr);
                            if (hr != null && hr.SuggestedMtu > 0) bgMtuReducedThisSession = true;
                            if (hr != null && hr.NeedReconnect && hr.SuggestedMtu > 0 && wasRunning == true)
                            {
                                if (!WindowShowing)
                                    Balloon("MTU adaptado para " + hr.SuggestedMtu + " B. Reconecte para aplicar.");
                                ShowBanner(true, "MTU adaptado para " + hr.SuggestedMtu + " bytes",
                                    "O log acusou pacotes acima do MTU. Reconecte para aplicar o ajuste automático.",
                                    "Reconectar agora", Reconnect, "Depois", HideBanner);
                            }
                            else if (hr != null && hr.FixedCount > 0)
                            {
                                UpdateState();
                                CheckIp();
                            }
                        }));
                    }
                    else Interlocked.Exchange(ref bgHealInFlight, 0);
                }
            });
        }

        void RecordWireSockLogEvent(string key, NetMon.EventKind kind, string message)
        {
            long lastTick;
            if (netMonLogCooldown.TryGetValue(key, out lastTick) && ElapsedSeconds(lastTick) < 10.0) return;
            netMonLogCooldown[key] = NowTicks();
            NetMon.Record("WireSock", kind, message);
        }

        void InspectHandshakeLine(string line, bool suppressEvents)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.IndexOf("Handshake response received", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Receiving handshake response", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool recovering = rehandshakeSuspectTick > 0;
                handshakeOk = sawHandshake = true;
                pendingSinceTick = null;
                rehandshakeSuspectTick = 0;
                rehandshakeGen++;
                rehandshakeConfirmedDown = false;
                rehandshakeOnlyChecked10s = false;
                activeProbeRunning = false;
                if (recovering && !suppressEvents) NetMon.Record("WireSock", NetMon.EventKind.Info, "Re-handshake WireGuard recuperado");
                return;
            }
            if (line.IndexOf("Handshake initiation sent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Sending handshake initiation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sawHandshake = true;
                // Only track initial pre-handshake pending window; post-handshake rekeys must not flap UI due to 4KB stdout buffering
                if (!handshakeOk && pendingSinceTick == null)
                    pendingSinceTick = NowTicks();
                return;
            }
            if (line.IndexOf("Handshake did not complete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sawHandshake = true;
                if (!handshakeOk)
                {
                    if (pendingSinceTick == null) pendingSinceTick = NowTicks();
                }
                else if (rehandshakeSuspectTick == 0)
                {
                    rehandshakeSuspectTick = NowTicks();
                    rehandshakeGen++;
                    rehandshakeOnlyChecked10s = false;
                    if (!suppressEvents) NetMon.Record("WireSock", NetMon.EventKind.Warn, "Handshake WireGuard não completou — verificando túnel");
                }
                return;
            }
            if (suppressEvents) return;

            string level;
            string msg = ExtractLogMessage(line, out level);
            if (string.IsNullOrEmpty(msg)) return;
            string low = msg.ToLowerInvariant();

            if (low.Contains("failed to inject bypass packet") || low.Contains("failed to enqueue packet for deferred processing"))
                RecordWireSockLogEvent("bypass", NetMon.EventKind.Error, "Driver falhou ao reinjetar pacote na placa física (bypass/fila)");
            else if (low.Contains("packet size is above mtu"))
            {
                if (NowTicks() > Interlocked.Read(ref suppressMtuLogUntilTick))
                {
                    RecordWireSockLogEvent("mtu", NetMon.EventKind.Warn, "Pacote excedeu MTU configurado no túnel");
                    if (settings.AutoHeal && settings.OptimizeMtu && wasRunning == true && handshakeOk && !bgMtuReducedThisSession)
                    {
                        if (mtuLogWindowStartTick == 0 || ElapsedSeconds(mtuLogWindowStartTick) > 30.0)
                        {
                            mtuLogWindowStartTick = NowTicks();
                            mtuLogBurstCount = 1;
                        }
                        else
                        {
                            mtuLogBurstCount++;
                        }
                        if (mtuLogBurstCount >= 3)
                        {
                            mtuLogBurstCount = 0;
                            mtuLogWindowStartTick = 0;
                            QueueBackgroundAutoHeal(true, true);
                        }
                    }
                }
            }
            else if (low.Contains("ip_interface_changed_callback") || low.Contains("network adapter list changed"))
                RecordWireSockLogEvent("flap", NetMon.EventKind.Warn, "WireSock detectou alteração/flap de adaptador e reiniciará o filtro");
            else if (low.Contains("watchdog") || low.Contains("tunnel restart failed"))
                RecordWireSockLogEvent("watchdog", NetMon.EventKind.Error, "Watchdog do WireSock: " + (msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg));
            else if (level == "error" || level == "fatal" || level == "critical")
                RecordWireSockLogEvent("err", NetMon.EventKind.Error, msg.Length > 120 ? msg.Substring(0, 120) + "..." : msg);
            else if (level == "warning" || level == "warn")
                RecordWireSockLogEvent("warn", NetMon.EventKind.Warn, msg.Length > 120 ? msg.Substring(0, 120) + "..." : msg);
            else if (string.IsNullOrEmpty(level) && (low.Contains("error") || low.Contains("exception") || low.Contains("failed") || low.Contains("fatal")))
                RecordWireSockLogEvent("err", NetMon.EventKind.Error, msg.Length > 120 ? msg.Substring(0, 120) + "..." : msg);
        }

        void ReadHandshakes()
        {
            try
            {
                if (logStream == null)
                {
                    if (!File.Exists(Paths.Log)) return;
                    logStream = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                    logDecoder.Reset();
                }
                long len = logStream.Length;
                bool isTailReplay = false;
                if (seekToTail)
                {
                    seekToTail = false;
                    isTailReplay = true;
                    if (len > 65536) logPos = len - 65536;
                }
                if (len < logPos)
                {
                    // log was recreated/truncated
                    logPos = 0;
                    logRest = "";
                    logDecoder.Reset();
                }
                if (len == logPos) return;
                logStream.Seek(logPos, SeekOrigin.Begin);
                var sb = new StringBuilder(logRest);
                while (logPos < len)
                {
                    int toRead = (int)Math.Min(logBuf.Length, len - logPos);
                    int n = logStream.Read(logBuf, 0, toRead);
                    if (n <= 0) break;
                    logPos += n;
                    int chars = logDecoder.GetChars(logBuf, 0, n, logChars, 0, false);
                    sb.Append(logChars, 0, chars);
                }
                string text = sb.ToString();
                int cut = text.LastIndexOf('\n');
                logRest = cut >= 0 ? text.Substring(cut + 1) : text;
                if (cut >= 0)
                {
                    foreach (string line in text.Substring(0, cut).Split('\n'))
                        InspectHandshakeLine(line, isTailReplay);
                }
            }
            catch
            {
                CloseLogStream();
            }
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

        void Poll()
        {
            if (!wireSockInstalled && !wireSockScanRunning && (lastWireSockBgCheckTick == 0 || ElapsedSeconds(lastWireSockBgCheckTick) >= 5))
            {
                wireSockScanRunning = true;
                lastWireSockBgCheckTick = NowTicks();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool found = Paths.WireSock != null;
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null)
                    {
                        ui.BeginInvoke(new Action(() =>
                        {
                            wireSockScanRunning = false;
                            if (exiting) return;
                            if (found != wireSockInstalled)
                            {
                                wireSockInstalled = found;
                                UpdateAbout();
                                UpdateSetup();
                            }
                        }));
                    }
                    else wireSockScanRunning = false;
                });
            }

            double scanInterval = WindowShowing ? 2.5 : 8.5;
            bool allowScan = wasRunning == true || connecting || reconnectPending || wasRunning == null ||
                             (DateTime.UtcNow - lastProcScan).TotalSeconds >= scanInterval;
            if (allowScan && wasRunning != true) lastProcScan = DateTime.UtcNow;
            bool running = Tunnel.Active(allowScan);

            if (running != wasRunning)
            {
                if (running)
                {
                    startedAt = Tunnel.StartTime;
                    connecting = false;
                    connectLaunchTick = 0;
                    // Fast path: if this instance just launched the tunnel, skip WMI
                    if (Tunnel.LastStartedConfig != null && (DateTime.UtcNow - Tunnel.LastStartedAt).TotalSeconds < 15)
                    {
                        runningConfig = Tunnel.LastStartedConfig;
                    }
                    else
                    {
                        int pid = Tunnel.Pid;
                        DateTime procStarted = startedAt;
                        if (pid != 0 && !cmdLineLookupRunning)
                        {
                            cmdLineLookupRunning = true;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string cmd = Tunnel.CommandLine(pid);
                                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                                if (ui != null)
                                {
                                    ui.BeginInvoke(new Action(() =>
                                    {
                                        cmdLineLookupRunning = false;
                                        if (exiting || wasRunning != true || Tunnel.Pid != pid)
                                        {
                                            startupIsolationCheckPending = false;
                                            return;
                                        }
                                        Tunnel.NoteCommandLine(cmd);
                                        runningConfig = Tunnel.ConfigOf(cmd);
                                        // a tunnel this app started before it was reopened still logs to our file
                                        if (!monitoring && cmd != null && cmd.Contains("-log-level info") && File.Exists(Paths.Log) &&
                                            File.GetLastWriteTime(Paths.Log) >= procStarted.AddSeconds(-5))
                                            StartMonitoring(false);
                                        if (runningConfig != null && (active == null || active.Name != runningConfig) && File.Exists(Paths.Conf(runningConfig)))
                                        {
                                            settings.Config = runningConfig;
                                            RefreshConfigs();
                                            LoadAppsEditor();
                                        }
                                        else if (settings.SharedApps && active != null && IsLive(active.Name))
                                        {
                                            appsNeedReconnect = !SameApps(active, settings.AppsMode, settings.Apps);
                                            UpdateAppsSaved();
                                        }
                                        if (startupIsolationCheckPending) CheckStartupIsolationAsync();
                                    }));
                                }
                                else cmdLineLookupRunning = false;
                            });
                        }
                    }
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
                    StopMonitoring();
                    Traffic.Stop();
                    if (!userStopped && !reconnectPending)
                    {
                        lastDropTick = NowTicks();
                        NetMon.Record("WireSock", NetMon.EventKind.Error,
                            "wiresock-client encerrou inesperadamente (código " + Tunnel.LastExitCode + ")");
                        bool willAutoReconnect = !quick && settings.AutoReconnect && reconnectAttempts < MaxReconnects;
                        if (willAutoReconnect)
                        {
                            // Serialize cleanup inside Connect(true) before Tunnel.Start so it never races the new tunnel
                            pendingPreConnectCleanup = true;
                            ThreadPool.QueueUserWorkItem(_ => ConfFile.DeleteRuntimeCopies());
                            reconnectAttempts++;
                            reconnectTimer.Start();
                        }
                        else
                        {
                            QueuePostDropCleanup();
                            if (quick) ShowError("Não foi possível conectar", LogTail());
                            else ShowError("A conexão caiu", reconnectAttempts > 0 ? "Tentei reconectar " + reconnectAttempts + " vezes sem sucesso. " + LogTail() : LogTail());
                        }
                    }
                    else
                    {
                        ThreadPool.QueueUserWorkItem(_ => ConfFile.DeleteRuntimeCopies());
                    }
                    userStopped = false;
                    runningConfig = null;
                }
                wasRunning = running;
                // the route to every server changed: old pings no longer apply
                Latency.RouteChanged();
                UpdateAllPingLabels();
                // connected: measured once the handshake completes (Conn.On below), not through a half-open tunnel
                if (!running) MeasureLatency(false);
                // a tunnel not started by Connect (other instance, .bat) may run an older shared list
                if (running && settings.SharedApps && active != null && IsLive(active.Name))
                    appsNeedReconnect = !SameApps(active, settings.AppsMode, settings.Apps);
                if (!running && reconnectPending)
                {
                    reconnectPending = false;
                    Connect(true);
                }
                else
                {
                    RefreshConfigs();
                }
            }

            if (running && monitoring)
            {
                ReadHandshakes();
                // a WireSock version that logs handshakes differently or whose 4 KB stdout buffer hasn't flushed yet:
                // assume initial connection succeeded after the grace window, keeping log monitoring active
                if (!sawHandshake && (DateTime.Now - startedAt).TotalSeconds > (logPos > 0 ? 10 : 25))
                {
                    handshakeOk = true;
                    pendingSinceTick = null;
                }
                bool pastResumeGrace = resumeGraceUntilTick == 0 || NowTicks() >= resumeGraceUntilTick;
                // Pre-handshake initial connection timeout (monotonic clock, paused on sleep)
                if (!handshakeOk && pendingSinceTick != null && pastResumeGrace && ElapsedSeconds(pendingSinceTick.Value) > GiveUpAfter)
                {
                    Disconnect(false);
                    bool hasMultiple = Paths.ConfigNames().Length > 1;
                    ShowBanner(false, "O servidor parou de responder",
                        "O handshake não completou a tempo. Tente reconectar ou troque para outro servidor.",
                        hasMultiple ? "Ir para o mais rápido" : "Tentar de novo",
                        hasMultiple ? (Action)ConnectFastest : Connect,
                        "Trocar servidor", () => tabServers.IsChecked = true);
                }
                // Post-handshake: verify with mode-appropriate probe if WireSock logged "Handshake did not complete"
                else if (handshakeOk && rehandshakeSuspectTick > 0 && pastResumeGrace)
                {
                    AppMode curMode = EffectiveAppMode();
                    if (curMode == AppMode.Only)
                    {
                        if (!rehandshakeOnlyChecked10s && !rehandshakeConfirmedDown && !activeProbeRunning && ElapsedSeconds(rehandshakeSuspectTick) > 10)
                        {
                            ProbeRehandshakeAsync(active);
                        }
                        else if (rehandshakeOnlyChecked10s && !rehandshakeConfirmedDown && !activeProbeRunning && ElapsedSeconds(rehandshakeSuspectTick) > 30)
                        {
                            rehandshakeConfirmedDown = true;
                            NetMon.Record("WireSock", NetMon.EventKind.Warn, "Re-handshake sem resposta há 30s (modo Só estes)");
                        }
                    }
                    else if (!rehandshakeConfirmedDown && !activeProbeRunning && ElapsedSeconds(rehandshakeSuspectTick) > 10)
                    {
                        ProbeRehandshakeAsync(active);
                    }

                    if (rehandshakeConfirmedDown && ElapsedSeconds(rehandshakeSuspectTick) > GiveUpAfter)
                    {
                        if (settings.AutoReconnect && reconnectAttempts < MaxReconnects)
                        {
                            reconnectAttempts++;
                            lastDropTick = NowTicks();
                            if (!WindowShowing)
                                Balloon("O servidor " + (active != null ? active.Name : "") + " parou de responder. Reconectando...");
                            Reconnect();
                        }
                        else
                        {
                            Disconnect(false);
                            bool hasMultiple = Paths.ConfigNames().Length > 1;
                            ShowBanner(false, "O servidor parou de responder",
                                "O servidor parou de responder após a conexão. Tente reconectar ou troque para o mais rápido.",
                                hasMultiple ? "Ir para o mais rápido" : "Tentar de novo",
                                hasMultiple ? (Action)ConnectFastest : Connect,
                                "Trocar servidor", () => tabServers.IsChecked = true);
                        }
                    }
                }
            }

            // the client exited before a poll saw it (skip while user reads modal or service stops)
            if (!running && connecting && !connectPausedForDialog)
            {
                bool launchTimedOut = connectLaunchTick > 0 && ElapsedSeconds(connectLaunchTick) > 8;
                bool totalTimedOut = ElapsedSeconds(connectTotalStartTick) > 35;
                if (launchTimedOut || totalTimedOut)
                {
                    connecting = false;
                    connectLaunchTick = 0;
                    ShowError("Não foi possível conectar", LogTail());
                }
            }

            Conn now = State;
            if (now != shown)
            {
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
                UpdateTraffic();
                if (WindowShowing) UpdateUptimeText();
                // Only reset auto-reconnect budget after sustained stability (5 min)
                if ((DateTime.Now - startedAt).TotalSeconds > 300 && (lastDropTick == 0 || ElapsedSeconds(lastDropTick) > 300) && now == Conn.On)
                    reconnectAttempts = 0;
                if (now == Conn.On && !string.IsNullOrEmpty(pendingPostHealDiagNote))
                {
                    string note = pendingPostHealDiagNote;
                    pendingPostHealDiagNote = null;
                    if (WindowShowing && pageDiag != null && pageDiag.Visibility == Visibility.Visible)
                        RunQuickDiag(note);
                }
            }
            else if (!connecting && !reconnectPending && !reconnectTimer.IsEnabled && !HasActiveOrPendingTunnel && !diagRepairRunning)
            {
                pendingPostHealDiagNote = null;
                if (settings.AutoHeal && Tunnel.HasPendingNetworkLock)
                    QueueBackgroundAutoHeal(false, false);
            }
            if (WindowShowing && pageHome.Visibility == Visibility.Visible)
            {
                if (!diagContinuousRunning && active != null && !string.IsNullOrEmpty(active.Endpoint) && (DateTime.UtcNow - lastHomePing).TotalSeconds >= 15)
                {
                    lastHomePing = DateTime.UtcNow;
                    Latency.Measure(new[] { active.Endpoint }, true, wasRunning == true);
                }
                if (now == Conn.On && active != null && active.Mode != AppMode.All && active.Apps.Count > 0 &&
                    (DateTime.UtcNow - lastHomeAppsCheck).TotalSeconds >= 3)
                {
                    lastHomeAppsCheck = DateTime.UtcNow;
                    RefreshRunningAppsAsync(active.Apps);
                }
            }
            if (WindowShowing && pageDiag != null && pageDiag.Visibility == Visibility.Visible)
                UpdateDiagTimeline();
            UpdatePollInterval();
        }

        void ProbeRehandshakeAsync(ConfigInfo cfgSnapshot)
        {
            activeProbeRunning = true;
            AppMode effMode = EffectiveAppMode();
            int seq = connectSeq;
            int hsGen = rehandshakeGen;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool tunnelAlive = false;
                bool epOk = false;
                try
                {
                    if (effMode != AppMode.Only)
                    {
                        // All/Except: raw UDP DNS traverses the tunnel (no Windows DNS cache, no TCP RST false OK)
                        NetDiag.ProbeResult udpRes = NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, CancellationToken.None);
                        tunnelAlive = udpRes != null && udpRes.Ok;
                        if (!tunnelAlive && cfgSnapshot != null && !string.IsNullOrEmpty(cfgSnapshot.Endpoint))
                        {
                            NetDiag.ProbeResult epRes = NetDiag.ProbeEndpoint(cfgSnapshot, 1500, CancellationToken.None);
                            epOk = epRes != null && epRes.Ok;
                            if (seq == connectSeq && hsGen == rehandshakeGen)
                                NetMon.Record("Túnel", NetMon.EventKind.Warn,
                                    "Túnel sem resposta UDP; endpoint físico " + (epOk ? "acessível" : "inacessível"));
                        }
                    }
                    else
                    {
                        // Only mode: VpnPanel bypasses the tunnel; ProbeEndpoint only checks physical internet vs server
                        if (cfgSnapshot != null && !string.IsNullOrEmpty(cfgSnapshot.Endpoint))
                        {
                            NetDiag.ProbeResult epRes = NetDiag.ProbeEndpoint(cfgSnapshot, 1500, CancellationToken.None);
                            epOk = epRes != null && epRes.Ok;
                        }
                        else epOk = true;
                    }
                }
                catch { tunnelAlive = false; epOk = false; }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (seq == connectSeq) activeProbeRunning = false;
                        if (exiting || seq != connectSeq || !monitoring || !handshakeOk || rehandshakeSuspectTick == 0 || hsGen != rehandshakeGen) return;
                        if (effMode == AppMode.Only)
                        {
                            rehandshakeOnlyChecked10s = true;
                            if (!epOk)
                            {
                                rehandshakeConfirmedDown = true;
                                NetMon.Record("Rede", NetMon.EventKind.Warn, "Re-handshake falhou e endpoint físico está inacessível");
                            }
                            return;
                        }
                        if (tunnelAlive)
                        {
                            rehandshakeSuspectTick = 0;
                            rehandshakeGen++;
                            rehandshakeConfirmedDown = false;
                        }
                        else
                        {
                            rehandshakeConfirmedDown = true;
                        }
                    }));
                }
                else if (seq == connectSeq) activeProbeRunning = false;
            });
        }

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
                else if (st == Conn.Retrying) NetMon.Record("Túnel", NetMon.EventKind.Warn, "Reconexão automática (tentativa " + reconnectAttempts + "/" + MaxReconnects + ")");
            }
            bool on = st == Conn.On;
            if (on && !Traffic.Running)
            {
                Traffic.Start(active != null ? active.Endpoint : null);
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

        void RefreshConfigs()
        {
            string[] names = Paths.ConfigNames();
            if (settings.Config == null || !names.Contains(settings.Config, StringComparer.OrdinalIgnoreCase))
            {
                settings.Config = names.FirstOrDefault();
                settings.Save();
            }
            active = settings.Config != null ? ConfigInfo.Read(names.First(n => n.Equals(settings.Config, StringComparison.OrdinalIgnoreCase))) : null;
            if (active != null && active.Endpoint != null && active.Place == null) Geo.Lookup(active.Endpoint, ScheduleGeoRefresh);

            // the cards are only built while someone can see them; hidden, they wait for OnVisibilityChanged or ShowPage
            if (ServersShowing) RenderServerList();
            else serversDirty = true;

            string code = active != null ? active.CountryCode : null;
            string flag = CountryFlag(code);
            txtFlag.Text = flag ?? (code ?? "");
            txtFlagGlobe.Visibility = (code != null || flag != null) ? Visibility.Collapsed : Visibility.Visible;
            txtSrvName.Text = active != null ? active.Name : "—";
            txtSrvDetail.Text = active == null ? "" : ServerDetail(active);
            txtSrvDetail.ToolTip = active == null ? null : active.Detail;
            UpdateHomePing();
            SyncDownloadsWatcher();
            UpdateLockAvailability();
            UpdateDiagRouteBadge();
            if (txtStatus != null && wasRunning != null) UpdateState();
        }

        void ScheduleGeoRefresh()
        {
            geoDebounce.Stop();
            geoDebounce.Start();
        }

        readonly List<KeyValuePair<ConfigInfo, UIElement>> serverCardItems = new List<KeyValuePair<ConfigInfo, UIElement>>();
        TextBlock noMatchLabel;

        void RenderServerList()
        {
            serversDirty = false;
            string[] names = Paths.ConfigNames();
            serverList.Children.Clear();
            serverCardItems.Clear();
            pingLabels.Clear();

            if (serverSearchBox != null)
                serverSearchBox.Visibility = names.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (string n in names)
            {
                ConfigInfo c = active != null && n == active.Name ? active : ConfigInfo.Read(n);
                if (c.Endpoint != null && c.Place == null) Geo.Lookup(c.Endpoint, ScheduleGeoRefresh);
                UIElement card = ServerCard(c);
                serverList.Children.Add(card);
                serverCardItems.Add(new KeyValuePair<ConfigInfo, UIElement>(c, card));
            }

            if (names.Length > 0)
            {
                if (noMatchLabel == null)
                {
                    noMatchLabel = new TextBlock
                    {
                        Text = "Nenhum servidor corresponde à pesquisa.",
                        Foreground = B("Muted"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 24, 0, 0),
                        Visibility = Visibility.Collapsed,
                    };
                }
                serverList.Children.Add(noMatchLabel);
            }

            FilterServerList();
            emptyServers.Visibility = names.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // a server just added gets measured too (the others are still fresh in the cache)
            MeasureLatency(false);
        }

        void FilterServerList()
        {
            if (serversDirty) { RenderServerList(); return; }
            string query = txtServerSearch != null ? txtServerSearch.Text.Trim() : null;
            int matches = 0;
            foreach (var kv in serverCardItems)
            {
                ConfigInfo c = kv.Key;
                bool match = true;
                if (!string.IsNullOrEmpty(query))
                {
                    match = c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match && c.Place != null)
                    {
                        if (c.Place.Country != null && c.Place.Country.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && c.Place.CountryName != null && c.Place.CountryName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && c.Place.City != null && c.Place.City.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    }
                    if (!match && c.GuessedCountry != null && c.GuessedCountry.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    if (!match && c.Server != null && c.Server.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    if (!match && c.Endpoint != null && c.Endpoint.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                }
                kv.Value.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) matches++;
            }
            if (noMatchLabel != null)
                noMatchLabel.Visibility = serverCardItems.Count > 0 && matches == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                Padding = new Thickness(12, 12, 10, 12),
            };
            AutomationProperties.SetName(card, "Usar " + c.Name);
            if (isActive) { card.BorderBrush = B("Accent"); card.Background = B("ActiveCard"); }

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string code = c.CountryCode;
            string flag = CountryFlag(code);
            var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = isActive ? B("AccentSoft") : B("Surface2"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
            badge.Child = flag != null
                ? (UIElement)new TextBlock { Text = flag, FontSize = 17, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : (code != null
                    ? (UIElement)new TextBlock { Text = code, FontWeight = FontWeights.Bold, Foreground = isActive ? B("Accent") : B("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    : (UIElement)new TextBlock { Text = "\uE774", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 16, Foreground = isActive ? B("Accent") : B("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(badge);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = c.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 100 });
            if (live || isActive)
            {
                var pill = new Border { Background = live ? B("AccentSoft") : B("Hover"), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pill.Child = new TextBlock { Text = live ? "Conectado" : "Selecionado", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = live ? B("Accent") : B("Muted") };
                titleRow.Children.Add(pill);
            }
            text.Children.Add(titleRow);
            text.Children.Add(new TextBlock { Text = ServerDetail(c), ToolTip = c.Detail, Style = Dialogs.S("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            if (!settings.SharedApps && c.Readable && c.Mode != AppMode.All)
                text.Children.Add(new TextBlock { Text = (c.Mode == AppMode.Only ? "Só " : "Exceto ") + JoinPt(c.Apps), Style = Dialogs.S("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (c.Endpoint != null)
            {
                var ping = new PingLabel(c.Endpoint);
                List<PingLabel> same;
                if (!pingLabels.TryGetValue(c.Endpoint, out same)) pingLabels[c.Endpoint] = same = new List<PingLabel>();
                same.Add(ping);
                UpdatePingLabel(ping);
                ping.Root.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(ping.Root);
            }
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

        // ---------- latency ----------

        // The ping shown on one server card: a colored dot and "42 ms"
        class PingLabel
        {
            public readonly string Host;
            public readonly StackPanel Root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MinWidth = 50, Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent };
            public readonly Ellipse Dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) };
            public readonly TextBlock Text = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

            public PingLabel(string host)
            {
                Host = host;
                Root.Children.Add(Dot);
                Root.Children.Add(Text);
                ToolTipService.SetInitialShowDelay(Root, 250);
            }
        }

        readonly Dictionary<string, List<PingLabel>> pingLabels = new Dictionary<string, List<PingLabel>>(StringComparer.OrdinalIgnoreCase);

        bool ServersShowing { get { return WindowShowing && pageServers.Visibility == Visibility.Visible; } }

        // Measures all servers while on Servidores, or just the active server while on Início
        void MeasureLatency(bool force)
        {
            if (ServersShowing)
            {
                var hosts = Paths.ConfigNames().Select(n => ConfigInfo.Read(n).Endpoint).Where(h => h != null).ToList();
                Latency.Measure(hosts, force, wasRunning == true);
                UpdateAllPingLabels(); // "···" on what was just queued
            }
            else if (WindowShowing && pageHome.Visibility == Visibility.Visible && active != null && !string.IsNullOrEmpty(active.Endpoint))
            {
                lastHomePing = DateTime.UtcNow;
                Latency.Measure(new[] { active.Endpoint }, force, wasRunning == true);
                UpdateHomePing();
            }
        }

        void OnLatencyMeasured(string host)
        {
            List<PingLabel> labels;
            if (pingLabels.TryGetValue(host, out labels))
                foreach (PingLabel l in labels) UpdatePingLabel(l);
            if (active != null && host.Equals(active.Endpoint, StringComparison.OrdinalIgnoreCase)) UpdateHomePing();
            TryConnectFastestPending();
        }

        void UpdateAllPingLabels()
        {
            foreach (List<PingLabel> labels in pingLabels.Values)
                foreach (PingLabel l in labels) UpdatePingLabel(l);
            UpdateHomePing();
        }

        static Brush PingBrush(int ms) { return B(ms < 80 ? "Accent" : ms <= 180 ? "Warn" : "Danger"); }

        // Through a full tunnel, pings to the other servers take the detour via the connected one
        string PingTip(string host, Latency.Result r)
        {
            if (!r.Ok)
                return "Sem resposta ao ping (ICMP).\nMuitos servidores WireGuard bloqueiam ping, então isso não quer dizer que o servidor esteja fora do ar.";
            string tip = "Latência (ping): " + r.Ms + " ms";
            bool liveHost = active != null && IsLive(active.Name) && host.Equals(active.Endpoint, StringComparison.OrdinalIgnoreCase);
            if (r.ViaTunnel && !liveHost) tip += "\nMedido com a VPN ligada: inclui o caminho pelo servidor conectado.";
            return tip;
        }

        void UpdatePingLabel(PingLabel l)
        {
            Latency.Result r = Latency.Cached(l.Host);
            if (r == null)
            {
                bool measuring = Latency.IsPending(l.Host);
                l.Dot.Visibility = Visibility.Collapsed;
                l.Text.Text = measuring ? "···" : "";
                l.Text.Foreground = B("Muted");
                l.Root.ToolTip = measuring ? "Medindo a latência..." : null;
                AutomationProperties.SetName(l.Root, measuring ? "Medindo latência" : "");
                return;
            }
            l.Dot.Visibility = r.Ok ? Visibility.Visible : Visibility.Collapsed;
            if (r.Ok) l.Dot.Fill = PingBrush(r.Ms);
            l.Text.Text = r.Ok ? r.Ms + " ms" : "sem ping";
            l.Text.Foreground = r.Ok ? PingBrush(r.Ms) : B("Muted");
            l.Root.ToolTip = PingTip(l.Host, r);
            AutomationProperties.SetName(l.Root, r.Ok ? "Latência " + r.Ms + " milissegundos" : "Servidor não responde a ping");
        }

        // Início only shows what Servidores already measured; it never pings by itself
        void UpdateHomePing()
        {
            Latency.Result r = active != null ? Latency.Cached(active.Endpoint) : null;
            if (r == null) { txtSrvPing.Visibility = Visibility.Collapsed; return; }
            txtSrvPing.Visibility = Visibility.Visible;
            txtSrvPing.Text = r.Ok ? "● " + r.Ms + " ms" : "sem ping";
            txtSrvPing.Foreground = r.Ok ? PingBrush(r.Ms) : B("Muted");
            txtSrvPing.ToolTip = PingTip(active.Endpoint, r);
        }

        bool IsLive(string name)
        {
            return wasRunning == true && runningConfig != null && runningConfig.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        void SelectConfig(string name)
        {
            if (active != null && active.Name == name) { tabHome.IsChecked = true; return; }
            StopContinuousDiag("Interrompido: servidor alterado");
            settings.Config = name;
            settings.Save();
            RefreshConfigs();
            LoadAppsEditor();
            // already connected: switch servers right away
            if (wasRunning == true) Reconnect();
            tabHome.IsChecked = true;
        }

        // From the tray: always ends up connected to that server (connects, or switches and reconnects)
        void SwitchServer(string name)
        {
            if (!File.Exists(Paths.Conf(name))) return;
            bool same = active != null && active.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
            if (!same)
            {
                StopContinuousDiag("Interrompido: servidor alterado");
                settings.Config = name;
                settings.Save();
                RefreshConfigs();
                LoadAppsEditor();
            }
            if (reconnectTimer.IsEnabled) reconnectTimer.Stop();
            if (wasRunning == true && userStopped)
            {
                // the old client is still exiting: Poll connects once it's gone
                reconnectPending = true;
                UpdateState();
            }
            else if (wasRunning == true) { if (!same) Reconnect(); }
            else if (connecting)
            {
                // started but not seen running yet: replace it instead of running two clients
                if (same) return;
                reconnectPending = true;
                Disconnect(false);
            }
            else Connect();
        }

        static List<ConfigInfo> ServersWithEndpoints(string[] names)
        {
            var list = new List<ConfigInfo>(names.Length);
            foreach (string n in names)
            {
                ConfigInfo c = ConfigInfo.Read(n);
                if (!string.IsNullOrEmpty(c.Endpoint)) list.Add(c);
            }
            return list;
        }

        static string FastestRatedServer(List<ConfigInfo> list)
        {
            string bestName = null;
            int bestMs = int.MaxValue;
            foreach (ConfigInfo c in list)
            {
                Latency.Result r = Latency.Cached(c.Endpoint);
                if (r != null && r.Ok && r.Ms < bestMs)
                {
                    bestMs = r.Ms;
                    bestName = c.Name;
                }
            }
            return bestName;
        }

        void ConnectFastest()
        {
            string[] names = Paths.ConfigNames();
            if (names.Length == 0)
            {
                if (WindowShowing) Dialogs.Info(w, "Nenhum servidor", "Adicione um servidor antes de conectar.");
                return;
            }
            if (names.Length == 1)
            {
                SwitchServer(names[0]);
                if (WindowShowing) tabHome.IsChecked = true;
                return;
            }

            List<ConfigInfo> list = ServersWithEndpoints(names);
            if (list.Count == 0)
            {
                SwitchServer(names[0]);
                if (WindowShowing) tabHome.IsChecked = true;
                return;
            }

            string best = FastestRatedServer(list);
            if (best != null)
            {
                SwitchServer(best);
                if (WindowShowing) tabHome.IsChecked = true;
                return;
            }

            bool allTested = list.All(s => Latency.Cached(s.Endpoint) != null);
            if (allTested)
            {
                SwitchServer(list[0].Name);
                if (WindowShowing)
                {
                    tabHome.IsChecked = true;
                    Dialogs.Info(w, "Latência indisponível", "Os servidores não respondem a ping ICMP. Conectando ao primeiro servidor.");
                }
                return;
            }

            // Pings not yet cached: measure asynchronously and connect automatically
            connectingFastestPending = true;
            fastestStarted = DateTime.UtcNow;
            if (fastestTimer.IsEnabled) fastestTimer.Stop();
            fastestTimer.Start();

            var hosts = list.Select(s => s.Endpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Latency.Measure(hosts, true, wasRunning == true);
            if (WindowShowing)
            {
                tabServers.IsChecked = true;
                UpdateAllPingLabels();
            }
            else
            {
                tray.ShowBalloonTip(2500, "VPN Panel", "Medindo latência dos servidores... Conectando ao mais rápido em instantes.", WF.ToolTipIcon.Info);
            }
        }

        void FinishConnectFastest()
        {
            if (!connectingFastestPending) return;
            connectingFastestPending = false;
            if (fastestTimer.IsEnabled) fastestTimer.Stop();

            List<ConfigInfo> list = ServersWithEndpoints(Paths.ConfigNames());
            if (list.Count == 0) return;

            string target = FastestRatedServer(list) ?? list[0].Name;
            SwitchServer(target);
            if (WindowShowing) tabHome.IsChecked = true;
        }

        void TryConnectFastestPending()
        {
            if (!connectingFastestPending) return;
            List<ConfigInfo> list = ServersWithEndpoints(Paths.ConfigNames());
            if (list.Count == 0)
            {
                connectingFastestPending = false;
                if (fastestTimer.IsEnabled) fastestTimer.Stop();
                return;
            }

            bool allDone = list.All(s => Latency.Cached(s.Endpoint) != null);
            string best = FastestRatedServer(list);

            if (allDone || (best != null && (DateTime.UtcNow - fastestStarted).TotalSeconds >= 1.5))
            {
                FinishConnectFastest();
            }
        }

        void RenameConfig(string name)
        {
            if (IsLive(name)) { Dialogs.Info(w, "Servidor em uso", "Desconecte a VPN antes de renomear este servidor."); return; }
            string newName = Dialogs.Prompt(w, "Renomear servidor", null, name, n => ConfFile.CheckName(n, name));
            if (newName == null || newName == name) return;
            try
            {
                ConfFile.Rename(name, newName);
                settings.RenameRecent(name, newName);
                if (settings.Config == name) settings.Config = newName;
                settings.Save();
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
            settings.Recent.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            settings.Save();
            RefreshConfigs();
            MigrateSharedApps();
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

        void ExportBackup()
        {
            try
            {
                if (!Directory.Exists(Paths.Configs) || !Directory.GetFiles(Paths.Configs, "*.conf").Any())
                {
                    Dialogs.Info(w, "Nenhum servidor", "Você ainda não tem nenhum servidor configurado para exportar.");
                    return;
                }
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Exportar backup dos servidores",
                    Filter = "Arquivo Zip (*.zip)|*.zip",
                    FileName = "VPN-Panel-Servidores-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip"
                };
                if (sfd.ShowDialog(w) == true)
                {
                    string tmpZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpn_backup_" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        System.IO.Compression.ZipFile.CreateFromDirectory(Paths.Configs, tmpZip);
                        if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                        File.Move(tmpZip, sfd.FileName);
                        Dialogs.Info(w, "Backup concluído", "Seus servidores foram salvos em:\n" + sfd.FileName);
                    }
                    finally
                    {
                        if (File.Exists(tmpZip)) { try { File.Delete(tmpZip); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                Dialogs.Info(w, "Erro no backup", "Não foi possível salvar o backup: " + ex.Message);
            }
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
            MigrateSharedApps();
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

        void SyncDownloadsWatcher()
        {
            if (downloadsWatcher == null) return;
            try { downloadsWatcher.EnableRaisingEvents = ExpectingDownload; } catch { }
        }

        void OpenGuide(bool otherProvider)
        {
            if (guide != null) { guide.Activate(); return; }
            expectDownloadUntil = DateTime.Now.AddMinutes(20);
            SyncDownloadsWatcher();
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
                FileSystemEventHandler seen = (s, e) =>
                {
                    if (e != null && ConfFile.IsImportable(e.FullPath))
                        w.Dispatcher.BeginInvoke(new Action(() => OnDownload(e.FullPath)));
                };
                downloadsWatcher.Created += seen;
                downloadsWatcher.Changed += seen;
                downloadsWatcher.Renamed += (s, e) =>
                {
                    if (e != null && ConfFile.IsImportable(e.FullPath))
                        w.Dispatcher.BeginInvoke(new Action(() => OnDownload(e.FullPath)));
                };
                downloadsWatcher.EnableRaisingEvents = ExpectingDownload;
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

        void ResetNetworkLockAction()
        {
            if (HasActiveOrPendingTunnel)
            {
                if (WindowShowing)
                {
                    bool ok = Dialogs.Confirm(w, "Túnel ativo",
                        "Para liberar a internet e resetar o bloqueio com segurança, é preciso desconectar a VPN antes.",
                        "Desconectar e liberar", true);
                    if (!ok) return;
                }
                DisconnectAndResetLock(() =>
                {
                    if (WindowShowing)
                        Dialogs.Info(w, "Internet liberada", "A VPN foi desconectada e o bloqueio de rede do WireSock foi desativado.");
                    else if (tray != null)
                        tray.ShowBalloonTip(3000, "Internet liberada", "VPN desconectada e bloqueio de rede desativado.", WF.ToolTipIcon.Info);
                });
                return;
            }
            NetMon.Record("Bloqueio", NetMon.EventKind.Warn, "Reset manual do bloqueio de rede (WFP)");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tunnel.ResetNetworkLock();
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui != null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        ipTimer.Stop();
                        ipTimer.Start();
                        if (WindowShowing)
                            Dialogs.Info(w, "Internet liberada", "O bloqueio de rede do WireSock foi desativado com sucesso.\nO tráfego de internet foi normalizado.");
                        else if (tray != null)
                            tray.ShowBalloonTip(3000, "Internet liberada", "O bloqueio de rede do WireSock foi desativado.", WF.ToolTipIcon.Info);
                    }));
                }
            });
        }

        // ---------- tray ----------

        static readonly WF.Padding TrayPadding = new WF.Padding(6, 4, 6, 4);
        const int TrayRecentCount = 3;

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
                    using (var icon32 = TrayIcon(true, false, preset.GdiPrimary, 32))
                    {
                        var icoSource = Imaging.CreateBitmapSourceFromHIcon(icon32.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        icoSource.Freeze();
                        titleIcon.Source = icoSource;
                        w.Icon = icoSource;
                    }
                }
                catch { }
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

        // Cyber Shield Raven icon (Mullvad style: solid accent circle with dark navy raven cutout for maximum clarity)
        static SD.Icon TrayIcon(bool active, bool warn, SD.Color? customAccent = null, int? targetSize = null)
        {
            int s = targetSize.HasValue ? targetSize.Value : Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
            using (var bmp = new SD.Bitmap(s, s))
            {
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

        void RecordNetworkAddressDelta(bool silent)
        {
            try
            {
                var parts = new List<string>();
                bool hasPhysicalGw = false;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                        ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    string v4Ip = "";
                    string gwIp = "";
                    try
                    {
                        var props = ni.GetIPProperties();
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                v4Ip = ua.Address.ToString();
                                break;
                            }
                        }
                        foreach (var gw in props.GatewayAddresses)
                        {
                            if (gw.Address != null && gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                !IPAddress.Any.Equals(gw.Address))
                            {
                                gwIp = gw.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(v4Ip) && string.IsNullOrEmpty(gwIp)) continue;
                    string descLow = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
                    bool isVirtual = descLow.Contains("wiresock") || descLow.Contains("wintun") || descLow.Contains("wireguard") ||
                                     descLow.Contains("tailscale") || descLow.Contains("warp") || descLow.Contains("hyper-v");
                    if (!isVirtual && !string.IsNullOrEmpty(gwIp)) hasPhysicalGw = true;
                    parts.Add(ni.Name + "=" + v4Ip + (string.IsNullOrEmpty(gwIp) ? "" : "/GW:" + gwIp));
                }
                parts.Sort(StringComparer.OrdinalIgnoreCase);
                string sig = string.Join(" | ", parts.ToArray());
                lock (netSigLock)
                {
                    if (sig == lastNetSignature) return;
                    lastNetSignature = sig;
                }
                if (silent) return;
                string summary = string.IsNullOrEmpty(sig) ? "Nenhuma interface IPv4 ativa" : sig;
                NetMon.Record("Rede", hasPhysicalGw ? NetMon.EventKind.Info : NetMon.EventKind.Warn,
                    "Mudança de interface/IP: " + summary);
            }
            catch { }
        }

        void OnDiagPageShown()
        {
            UpdateDiagRouteBadge();
            UpdateDiagTimeline();
            if (!diagQuickRunning && string.IsNullOrEmpty(lastDiagSummary))
                RunQuickDiag();
        }

        void UpdateDiagTimeline()
        {
            if (diagTimelineList == null || txtDiagTimelineEmpty == null) return;
            long v = NetMon.Version;
            if (v == lastTimelineVersion) return;
            lastTimelineVersion = v;

            NetMon.EventItem[] items = NetMon.Snapshot(10);
            diagTimelineList.Children.Clear();
            if (items == null || items.Length == 0)
            {
                txtDiagTimelineEmpty.Visibility = Visibility.Visible;
                diagTimelineList.Visibility = Visibility.Collapsed;
                return;
            }
            txtDiagTimelineEmpty.Visibility = Visibility.Collapsed;
            diagTimelineList.Visibility = Visibility.Visible;

            for (int i = items.Length - 1; i >= 0; i--)
            {
                NetMon.EventItem it = items[i];
                var row = new Grid { Margin = new Thickness(0, 0, 0, i == 0 ? 0 : 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                string brushKey = it.Kind == NetMon.EventKind.Error ? "Danger"
                                : it.Kind == NetMon.EventKind.Warn ? "Warn"
                                : "SecureText";
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = B(brushKey),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 5, 8, 0)
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var txt = new TextBlock
                {
                    Text = string.Format("{0:HH:mm:ss} [{1}] {2}", it.Time, it.Category, it.Message),
                    FontSize = 11.5,
                    Foreground = it.Kind == NetMon.EventKind.Error ? B("Danger") : it.Kind == NetMon.EventKind.Warn ? B("Warn") : B("Muted"),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(txt, 1);
                row.Children.Add(txt);
                diagTimelineList.Children.Add(row);
            }
        }

        static NetDiag.ProbeResult ProbeIcmpSingle(IPAddress ip, int timeoutMs, CancellationToken ct)
        {
            if (ip == null) return new NetDiag.ProbeResult { Ok = false, LatencyMs = -1, Target = "", Detail = "Sem gateway" };
            if (ct.IsCancellationRequested) return new NetDiag.ProbeResult { Ok = false, LatencyMs = -1, Target = ip.ToString(), Detail = "Cancelado" };
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var rep = ping.Send(ip, Math.Max(300, timeoutMs));
                    if (rep != null && rep.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        return new NetDiag.ProbeResult
                        {
                            Ok = true,
                            LatencyMs = Math.Max(1, (int)rep.RoundtripTime),
                            Target = ip.ToString(),
                            Detail = "ICMP OK"
                        };
                    }
                    return new NetDiag.ProbeResult
                    {
                        Ok = false,
                        LatencyMs = -1,
                        Target = ip.ToString(),
                        Detail = rep != null ? rep.Status.ToString() : "Sem resposta"
                    };
                }
            }
            catch (Exception ex)
            {
                return new NetDiag.ProbeResult { Ok = false, LatencyMs = -1, Target = ip.ToString(), Detail = ex.Message };
            }
        }

        static void ScanRecentLogSignals(out string driverErr, out string logWarn, out bool hasBypassFail, out bool hasMtuErr, out bool hasAdapterFlap)
        {
            driverErr = null;
            logWarn = null;
            hasBypassFail = false;
            hasMtuErr = false;
            hasAdapterFlap = false;
            try
            {
                if (!File.Exists(Paths.Log)) return;
                using (var fs = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > 32768) fs.Seek(fs.Length - 32768, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs))
                    {
                        string[] lines = r.ReadToEnd().Split('\n');
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            string l = lines[i].Trim();
                            if (l.Length == 0) continue;
                            string lvl;
                            string msg = ExtractLogMessage(l, out lvl);
                            if (string.IsNullOrEmpty(msg)) continue;
                            string low = msg.ToLowerInvariant();
                            if (low.Contains("failed to inject bypass packet") || low.Contains("failed to enqueue packet for deferred processing"))
                            {
                                hasBypassFail = true;
                                if (driverErr == null) driverErr = "Falha de reinjeção/bypass no driver WireSock";
                            }
                            if (low.Contains("packet size is above mtu"))
                            {
                                hasMtuErr = true;
                                if (logWarn == null) logWarn = "Pacote acima do MTU no túnel";
                            }
                            if (low.Contains("ip_interface_changed_callback") || low.Contains("network adapter list changed") || low.Contains("watchdog"))
                            {
                                hasAdapterFlap = true;
                                if (logWarn == null) logWarn = "WireSock reiniciou filtro por alteração de adaptador";
                            }
                            if (driverErr == null && (lvl == "error" || lvl == "fatal" || lvl == "critical"))
                                driverErr = msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg;
                            if (logWarn == null && (lvl == "warning" || lvl == "warn"))
                                logWarn = msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg;
                        }
                    }
                }
            }
            catch { }
        }

        void UpdateDiagRouteBadge()
        {
            if (diagRouteBadge == null || txtDiagRouteBadge == null) return;
            AppMode eff = EffectiveAppMode();
            string srvName = active != null ? active.Name : "sem servidor";
            if (wasRunning == true)
            {
                if (eff == AppMode.Only)
                {
                    txtDiagRouteBadge.Text = "Modo Só estes — testes vão por fora · VPN (" + srvName + "): " + (handshakeOk ? "ativa" : "conectando");
                    diagRouteBadge.Background = B("WarnSoft");
                    txtDiagRouteBadge.Foreground = B("Warn");
                }
                else if (eff == AppMode.Except)
                {
                    txtDiagRouteBadge.Text = "Rota do teste: Pela VPN (" + srvName + " · Exceto lista)";
                    diagRouteBadge.Background = handshakeOk ? B("SecureSoft") : B("WarnSoft");
                    txtDiagRouteBadge.Foreground = handshakeOk ? B("SecureText") : B("Warn");
                }
                else
                {
                    txtDiagRouteBadge.Text = "Rota do teste: Pela VPN (" + srvName + " · Todos)";
                    diagRouteBadge.Background = handshakeOk ? B("SecureSoft") : B("WarnSoft");
                    txtDiagRouteBadge.Foreground = handshakeOk ? B("SecureText") : B("Warn");
                }
            }
            else
            {
                txtDiagRouteBadge.Text = "Rota do teste: Internet direta (VPN desconectada)";
                diagRouteBadge.Background = B("Surface2");
                txtDiagRouteBadge.Foreground = B("Muted");
            }
        }

        void CancelQuickDiag(bool disableBtn)
        {
            diagEpoch++;
            diagQuickRunning = false;
            if (diagQuickCts != null)
            {
                try { diagQuickCts.Cancel(); } catch { }
                diagQuickCts = null;
            }
            if (btnDiagQuick != null)
            {
                btnDiagQuick.IsEnabled = !disableBtn && !diagRepairRunning;
                btnDiagQuick.Content = "Testar agora";
            }
        }

        void CancelAllDiag()
        {
            CancelQuickDiag(false);
            diagContEpoch++;
            diagContinuousRunning = false;
            if (diagContCts != null)
            {
                try { diagContCts.Cancel(); } catch { }
                diagContCts = null;
            }
            if (btnDiagRepair != null)
            {
                btnDiagRepair.IsEnabled = true;
                btnDiagRepair.Content = "Corrigir problemas encontrados";
            }
            if (btnDiagContinuous != null) btnDiagContinuous.Content = "Teste contínuo";
        }

        static void SetDiagDot(Ellipse dot, string brushKey)
        {
            if (dot != null) dot.Fill = B(brushKey);
        }

        static NetDiag.AuditItem FindAudit(List<NetDiag.AuditItem> items, string key)
        {
            if (items == null) return null;
            foreach (var item in items)
                if (item.Key == key) return item;
            return null;
        }

        void RunQuickDiag() { RunQuickDiag(null); }

        void RunQuickDiag(string postFixNote)
        {
            if (exiting || pageDiag == null) return;
            UpdateDiagRouteBadge();

            if (diagQuickCts != null)
            {
                try { diagQuickCts.Cancel(); } catch { }
            }
            CancellationTokenSource myCts = new CancellationTokenSource();
            diagQuickCts = myCts;
            CancellationToken ct = myCts.Token;
            int myEpoch = ++diagEpoch;

            diagQuickRunning = true;
            btnDiagQuick.IsEnabled = false;
            btnDiagQuick.Content = "Testando...";
            btnDiagRepair.IsEnabled = true;
            btnDiagRepair.Content = "Corrigir problemas encontrados";
            btnDiagRepair.Visibility = Visibility.Collapsed;
            btnDiagApps.Visibility = active != null ? Visibility.Visible : Visibility.Collapsed;

            SetDiagDot(diagVerdictDot, "Muted");
            txtDiagVerdict.Foreground = B("Text");
            txtDiagVerdict.Text = "Analisando conexão...";
            txtDiagVerdictSub.Text = "Testando internet, UDP, DNS, gateway, MTU, conflitos e logs...";
            SetDiagDot(chkItem1Dot, "Muted"); txtChk1Detail.Text = "Testando acesso HTTPS, UDP e gateway...";
            SetDiagDot(chkItem2Dot, "Muted"); txtChk2Detail.Text = "Testando resolução de nomes...";
            SetDiagDot(chkItem3Dot, "Muted"); txtChk3Detail.Text = "Verificando servidor, túnel e log...";
            SetDiagDot(chkItem4Dot, "Muted"); txtChk4Detail.Text = "Verificando kill switch e IPv6...";
            SetDiagDot(chkItem5Dot, "Muted"); txtChk5Detail.Text = "Auditando configuração e apps...";
            SetDiagDot(chkItem6Dot, "Muted"); txtChk6Detail.Text = "Testando fragmentação MTU (1472/1392/1352)...";
            SetDiagDot(chkItem7Dot, "Muted"); txtChk7Detail.Text = "Verificando serviços, drivers e adaptadores...";

            ConfigInfo cfgSnap = active;
            var sSnap = new Settings
            {
                NetworkLock = settings.NetworkLock,
                OptimizeMtu = settings.OptimizeMtu,
                AdaptiveMtu = settings.AdaptiveMtu,
                AutoHeal = settings.AutoHeal,
                SharedApps = settings.SharedApps,
                AppsMode = settings.AppsMode,
                Apps = new List<string>(settings.Apps)
            };
            bool tunnelRunning = wasRunning == true;
            bool hsOk = handshakeOk;
            bool v6Isolated = Tunnel.Ipv6Isolated;
            bool wsInstalledSnap = wireSockInstalled;
            bool appsPending = appsNeedReconnect;
            AppMode effMode = EffectiveAppMode();
            int effMtuSnap = DiagEffectiveMtu(tunnelRunning, effMode, sSnap);
            TimeSpan up = (tunnelRunning && hsOk) ? (DateTime.Now - startedAt) : TimeSpan.Zero;
            string uptimeStr = (tunnelRunning && hsOk)
                ? string.Format("{0:00}:{1:00}:{2:00}", (int)up.TotalHours, up.Minutes, up.Seconds)
                : "";
            string logHint = (!hsOk || !tunnelRunning) ? LogTail() : "";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool uiPosted = false;
                try
                {
                    if (ct.IsCancellationRequested) return;
                    string driverLogErr, driverLogWarn;
                    bool logBypassFail, logMtuErr, logAdapterFlap;
                    ScanRecentLogSignals(out driverLogErr, out driverLogWarn, out logBypassFail, out logMtuErr, out logAdapterFlap);

                    bool hasGhostV6 = ConfFile.HasGhostIpv6();
                    bool pendingLock = Tunnel.HasPendingNetworkLock;
                    List<NetDiag.AuditItem> audit = NetDiag.AuditConfig(cfgSnap, sSnap, hasGhostV6, tunnelRunning, appsPending);
                    NetDiag.ProbeResult httpRes = NetDiag.ProbeHttp204(3000, ct);
                    NetDiag.ProbeResult dnsRes = NetDiag.ProbeDns("cloudflare.com", ct);
                    NetDiag.ProbeResult udpRes = NetDiag.ProbeUdpDns("1.1.1.1", 53, 1500, ct);
                    IPAddress gwIp = NetDiag.GetPhysicalGateway();
                    NetDiag.ProbeResult gwRes = gwIp != null ? ProbeIcmpSingle(gwIp, 1200, ct) : null;
                    NetDiag.ProbeResult epRes = cfgSnap != null ? NetDiag.ProbeEndpoint(cfgSnap, 2000, ct) : null;
                    NetDiag.MtuResult mtuRes;
                    try
                    {
                        Interlocked.Exchange(ref suppressMtuLogUntilTick, FutureTicks(25.0));
                        mtuRes = NetDiag.ProbeMtu(IPAddress.Parse("1.1.1.1"), ct);
                        if (!mtuRes.Ok && !ct.IsCancellationRequested &&
                            mtuRes.Detail != null && mtuRes.Detail.IndexOf("ICMP bloqueado", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mtuRes = NetDiag.ProbeMtu(IPAddress.Parse("8.8.8.8"), ct);
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref suppressMtuLogUntilTick, FutureTicks(1.5));
                    }
                    NetDiag.ConflictReport conflicts = NetDiag.DetectConflicts();
                    int suggestedMtu = AutoHealer.SuggestSafeMtu(mtuRes, logMtuErr, effMtuSnap);
                    bool canFixMtu = suggestedMtu > 0 && (!sSnap.OptimizeMtu || effMtuSnap == 0 || suggestedMtu < effMtuSnap);

                    // Recent NetMon events (last 5 minutes)
                    DateTime cutoff = DateTime.Now.AddMinutes(-5);
                    NetMon.EventItem[] recentEvents = NetMon.Snapshot(40);
                    int recentNetFlaps = 0, recentDriverWarns = 0;
                    for (int i = 0; i < recentEvents.Length; i++)
                    {
                        if (recentEvents[i].Time < cutoff) continue;
                        if (recentEvents[i].Category == "Rede" && recentEvents[i].Kind != NetMon.EventKind.Info) recentNetFlaps++;
                        if (recentEvents[i].Category == "WireSock" && recentEvents[i].Kind != NetMon.EventKind.Info) recentDriverWarns++;
                    }

                    bool cancelled = ct.IsCancellationRequested;
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui == null) return;
                    uiPosted = true;
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (exiting || myEpoch != diagEpoch) return;
                        diagQuickRunning = false;
                        btnDiagQuick.IsEnabled = !diagRepairRunning;
                        btnDiagQuick.Content = "Testar agora";
                        if (cancelled || diagRepairRunning) return;
                        UpdateDiagRouteBadge();

                        // 1. Internet Base (HTTP 204 + UDP DNS + Gateway)
                        string gwTag = gwRes == null ? "" : (gwRes.Ok ? " · GW " + gwRes.LatencyMs + " ms" : " · GW falhou (" + gwRes.Detail + ")");
                        string udpTag = udpRes.Ok ? " · UDP " + udpRes.LatencyMs + " ms" : " · UDP falhou (" + udpRes.Detail + ")";
                        string d1;
                        if (httpRes.Ok && udpRes.Ok)
                        {
                            SetDiagDot(chkItem1Dot, "SecureText");
                            d1 = "OK em " + httpRes.LatencyMs + " ms" + udpTag + gwTag +
                                 (tunnelRunning && effMode == AppMode.Only ? " — via bypass" : "");
                        }
                        else if (httpRes.Ok && !udpRes.Ok)
                        {
                            SetDiagDot(chkItem1Dot, "Warn");
                            d1 = "HTTP OK (" + httpRes.LatencyMs + " ms), mas UDP:53 falhou (" + udpRes.Detail + ")" + gwTag;
                        }
                        else if (!tunnelRunning && pendingLock)
                        {
                            SetDiagDot(chkItem1Dot, "Danger");
                            d1 = "Sem acesso (" + httpRes.Detail + ") — bloqueio de rede pendente detectado.";
                        }
                        else
                        {
                            SetDiagDot(chkItem1Dot, "Danger");
                            d1 = "Falhou (" + httpRes.Detail + ")" + udpTag + gwTag;
                        }
                        txtChk1Detail.Text = d1;

                        // 2. DNS do Windows
                        string d2;
                        if (dnsRes.Ok)
                        {
                            SetDiagDot(chkItem2Dot, "SecureText");
                            d2 = "Resolvido em " + dnsRes.LatencyMs + " ms (" + dnsRes.Target + ")";
                        }
                        else
                        {
                            SetDiagDot(chkItem2Dot, "Danger");
                            d2 = "Falhou (" + dnsRes.Detail + ")" + (udpRes.Ok ? " — DNS UDP bruto respondeu, possível trava no Dnscache." : "");
                        }
                        txtChk2Detail.Text = d2;

                        // 3. Túnel & Servidor VPN
                        string d3;
                        string logExtra = !string.IsNullOrEmpty(driverLogErr) ? (" · Log: " + driverLogErr)
                                        : !string.IsNullOrEmpty(driverLogWarn) ? (" · Log: " + driverLogWarn) : "";
                        if (cfgSnap == null)
                        {
                            SetDiagDot(chkItem3Dot, "Warn");
                            d3 = "Nenhum servidor selecionado.";
                        }
                        else if (tunnelRunning && hsOk)
                        {
                            if (epRes != null && epRes.Ok)
                            {
                                SetDiagDot(chkItem3Dot, !string.IsNullOrEmpty(driverLogErr) ? "Warn" : "SecureText");
                                d3 = "Conectado há " + uptimeStr + " · endpoint " + epRes.LatencyMs + " ms" + logExtra;
                            }
                            else
                            {
                                SetDiagDot(chkItem3Dot, "Warn");
                                d3 = "Túnel ativo há " + uptimeStr + " (endpoint sem resposta a ICMP/TCP: " +
                                     (epRes != null ? epRes.Detail : "sem resposta") + ")" + logExtra;
                            }
                        }
                        else if (tunnelRunning)
                        {
                            SetDiagDot(chkItem3Dot, "Warn");
                            d3 = "WireSock aberto, aguardando handshake · endpoint: " +
                                 (epRes != null && epRes.Ok ? epRes.LatencyMs + " ms" : (epRes != null ? epRes.Detail : "sem resposta")) + logExtra;
                        }
                        else
                        {
                            if (epRes != null && epRes.Ok)
                            {
                                SetDiagDot(chkItem3Dot, "SecureText");
                                d3 = "Desconectado · endpoint acessível em " + epRes.LatencyMs + " ms (" + epRes.Target + ")";
                            }
                            else
                            {
                                SetDiagDot(chkItem3Dot, "Danger");
                                d3 = "Desconectado · endpoint inacessível (" + (epRes != null ? epRes.Detail : "erro") + ")";
                            }
                        }
                        txtChk3Detail.Text = d3;

                        // 4. Kill Switch & IPv6
                        bool ghostAlert = hasGhostV6 && (!tunnelRunning || v6Isolated);
                        NetDiag.AuditItem lockFlagItem = FindAudit(audit, "lock_flag");
                        NetDiag.AuditItem ghostV6Item = FindAudit(audit, "ghost_v6");
                        NetDiag.AuditItem lockSplitItem = FindAudit(audit, "lock_split");
                        bool item4HasErr = false;
                        bool item4HasWarn = false;
                        string d4;
                        if (lockFlagItem != null || (!tunnelRunning && pendingLock))
                        {
                            item4HasErr = true;
                            SetDiagDot(chkItem4Dot, "Danger");
                            d4 = lockFlagItem != null ? lockFlagItem.Detail : "Bloqueio residual do Kill Switch (WFP) ativo sem VPN.";
                        }
                        else if (ghostAlert && ghostV6Item != null && ghostV6Item.Severity != NetDiag.AuditSeverity.Ok)
                        {
                            item4HasWarn = true;
                            SetDiagDot(chkItem4Dot, "Warn");
                            d4 = ghostV6Item.Detail;
                        }
                        else if (lockSplitItem != null && lockSplitItem.Severity != NetDiag.AuditSeverity.Ok)
                        {
                            item4HasWarn = true;
                            SetDiagDot(chkItem4Dot, "Warn");
                            d4 = lockSplitItem.Detail;
                        }
                        else
                        {
                            SetDiagDot(chkItem4Dot, "SecureText");
                            d4 = (tunnelRunning && sSnap.NetworkLock && effMode == AppMode.All)
                                ? "Kill switch ativo com VPN conectada; sem rotas IPv6 fantasmas."
                                : "Sem bloqueio residual (WFP) e sem rota IPv6 fantasma.";
                        }
                        txtChk4Detail.Text = d4;

                        // 5. Configuração & Apps
                        var item5Problems = new List<string>();
                        bool item5HasErr = false;
                        bool item5HasWarn = false;
                        foreach (string k in new[] { "config", "keepalive", "mtu", "lan", "apps_pending" })
                        {
                            NetDiag.AuditItem it = FindAudit(audit, k);
                            if (it != null && it.Severity != NetDiag.AuditSeverity.Ok)
                            {
                                if (it.Severity == NetDiag.AuditSeverity.Error) item5HasErr = true;
                                else item5HasWarn = true;
                                item5Problems.Add(it.Detail);
                            }
                        }
                        int appCount = settings.SharedApps ? sSnap.Apps.Count : (cfgSnap != null ? cfgSnap.Apps.Count : 0);
                        string modeLabel = effMode == AppMode.All ? "Todos os apps"
                                         : effMode == AppMode.Only ? "Só estes (" + appCount + ")"
                                         : "Exceto (" + appCount + ")";
                        string d5;
                        if (item5Problems.Count > 0)
                        {
                            SetDiagDot(chkItem5Dot, item5HasErr ? "Danger" : "Warn");
                            d5 = modeLabel + ". " + string.Join(" ", item5Problems.ToArray());
                        }
                        else
                        {
                            SetDiagDot(chkItem5Dot, "SecureText");
                            d5 = "Configuração saudável (" + modeLabel + ", keepalive e MTU OK).";
                        }
                        txtChk5Detail.Text = d5;

                        // 6. MTU & Pacotes de Jogos
                        bool mtuIntentionalCap = sSnap.OptimizeMtu && effMtuSnap > 0 && !logMtuErr &&
                                                 mtuRes.Ok && mtuRes.MaxPayload >= effMtuSnap - 28;
                        bool mtuBlockedOnly = !mtuRes.Ok && mtuRes.Detail != null && mtuRes.Detail.IndexOf("ICMP bloqueado", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool mtuFrag = !mtuIntentionalCap && ((!mtuRes.Ok && !mtuBlockedOnly) || (mtuRes.Ok && mtuRes.MaxPayload < 1472) || logMtuErr);
                        string d6;
                        if (mtuIntentionalCap)
                        {
                            d6 = (sSnap.AdaptiveMtu > 0 ? "MTU adaptativo (" + effMtuSnap + " B)" : "MTU otimizado (" + effMtuSnap + " B)") +
                                 " ativo e sem fragmentação (" + mtuRes.Detail + ").";
                            SetDiagDot(chkItem6Dot, udpRes.Ok ? "SecureText" : "Warn");
                        }
                        else
                        {
                            d6 = mtuRes.Detail;
                            if (logMtuErr)
                                d6 += " · Log do WireSock acusou pacote acima do MTU.";
                            if (mtuRes.Ok && mtuRes.MaxPayload == 1472 && !logMtuErr)
                                SetDiagDot(chkItem6Dot, udpRes.Ok ? "SecureText" : "Warn");
                            else if (mtuFrag)
                                SetDiagDot(chkItem6Dot, (!mtuRes.Ok || logMtuErr) ? "Danger" : "Warn");
                            else
                                SetDiagDot(chkItem6Dot, "Muted");
                        }
                        txtChk6Detail.Text = d6;

                        // 7. Conflitos & Driver
                        bool hardConflict = conflicts.WireSockServiceRunning || conflicts.WireSockProcessCount > 1 ||
                                            conflicts.ConflictingAdapters.Count > 0 || conflicts.ConflictingServices.Count > 0;
                        bool driverMissing = wsInstalledSnap && !conflicts.NdisDriverPresent;
                        bool anyConflictWarn = hardConflict || driverMissing || conflicts.LowMetricAdapters.Count > 0 || logBypassFail || logAdapterFlap;
                        string d7;
                        if (conflicts.Warnings.Count > 0)
                        {
                            SetDiagDot(chkItem7Dot, (hardConflict || driverMissing) ? "Danger" : "Warn");
                            d7 = string.Join(" ", conflicts.Warnings.ToArray());
                        }
                        else if (logBypassFail || logAdapterFlap)
                        {
                            SetDiagDot(chkItem7Dot, logBypassFail ? "Danger" : "Warn");
                            d7 = logBypassFail ? "Sem conflitos externos, mas o driver falhou ao reinjetar pacotes (bypass)."
                                               : "Sem conflitos externos, mas houve oscilação/reinício de adaptador no log.";
                        }
                        else
                        {
                            SetDiagDot(chkItem7Dot, "SecureText");
                            d7 = "Driver ndiswgc ativo · sem VPNs, serviços ou rotas concorrentes.";
                        }
                        txtChk7Detail.Text = d7;

                        int fixableCount = 0;
                        if (!tunnelRunning && pendingLock) fixableCount++;
                        if (ghostAlert) fixableCount++;
                        if (!dnsRes.Ok) fixableCount++;
                        if (conflicts.WireSockServiceRunning) fixableCount++;
                        if ((!tunnelRunning && conflicts.WireSockProcessCount > 0) || (tunnelRunning && conflicts.WireSockProcessCount > 1)) fixableCount++;
                        if (conflicts.ConflictingServices.Count > 0) fixableCount += conflicts.ConflictingServices.Count;
                        if (canFixMtu) fixableCount++;

                        lastDiagMtu = mtuRes;
                        lastDiagConflicts = conflicts;
                        lastDiagDnsFailed = !dnsRes.Ok;
                        lastDiagLogMtuErr = logMtuErr;
                        lastDiagFixableCount = fixableCount;

                        bool showRepair = fixableCount > 0 || (!httpRes.Ok && !dnsRes.Ok && !tunnelRunning);
                        btnDiagRepair.Content = fixableCount > 0 ? ("Corrigir problemas encontrados (" + fixableCount + ")") : "Reparar internet agora";
                        btnDiagRepair.Visibility = showRepair ? Visibility.Visible : Visibility.Collapsed;

                        // Motor de Veredito Causa-Raiz (Tabela de prioridade em PT-BR claro)
                        string vColor = "SecureText";
                        string vTitle, vSub;
                        bool gwBlockedByTunnel = (epRes != null && epRes.Ok) || httpRes.Ok || udpRes.Ok || (tunnelRunning && effMode == AppMode.All);
                        bool physicalGwDown = gwRes != null && !gwRes.Ok && !gwBlockedByTunnel;
                        bool allNetDown = !httpRes.Ok && !dnsRes.Ok && !udpRes.Ok;
                        bool endpointDown = epRes != null && !epRes.Ok;

                        if (!tunnelRunning && pendingLock)
                        {
                            vColor = "Danger";
                            vTitle = "Internet bloqueada pelo Kill Switch";
                            vSub = "Um bloqueio residual (WFP) permaneceu ativo sem a VPN. Clique em \"Reparar internet agora\".";
                        }
                        else if (hardConflict)
                        {
                            vColor = "Danger";
                            vTitle = "Conflito com outro serviço VPN / driver";
                            vSub = conflicts.Warnings.Count > 0 ? conflicts.Warnings[0] : "Feche a outra VPN ou serviço concorrente para estabilizar jogos e apps.";
                        }
                        else if (physicalGwDown || (allNetDown && (endpointDown || !tunnelRunning || effMode == AppMode.Only)))
                        {
                            vColor = "Danger";
                            vTitle = "A falha foi na sua internet / Wi-Fi";
                            vSub = physicalGwDown
                                ? "Seu roteador/gateway físico (" + gwRes.Target + ") não respondeu. Verifique Wi-Fi, cabo ou modem."
                                : ghostAlert
                                    ? "Sem resposta externa e há rota IPv6 fantasma. Verifique o Wi-Fi ou clique em \"Reparar internet agora\"."
                                    : "Sem resposta de DNS, UDP nem HTTPS na conexão física. Verifique seu Wi-Fi ou operadora.";
                        }
                        else if (driverMissing || logBypassFail || (tunnelRunning && effMode == AppMode.Only && (!udpRes.Ok || !httpRes.Ok)))
                        {
                            vColor = "Danger";
                            vTitle = "A falha foi no WireSock / driver derrubando a placa";
                            vSub = driverMissing
                                ? "O driver ndiswgc do WireSock não está ativo. Reinstale o WireSock e reinicie o PC."
                                : "O filtro do WireSock na placa física teve falha de bypass/reinício (" + (driverLogErr ?? udpRes.Detail) + ").";
                        }
                        else if ((!mtuIntentionalCap && ((!mtuRes.Ok && !mtuBlockedOnly) || logMtuErr)) || (httpRes.Ok && !udpRes.Ok))
                        {
                            vColor = "Warn";
                            vTitle = "Fragmentação de MTU / perda UDP para jogos";
                            vSub = !udpRes.Ok
                                ? "Navegação HTTP passou, mas pacotes UDP (jogos como Roblox) falharam. Rode o Teste contínuo."
                                : "Pacotes grandes estão fragmentando (" + mtuRes.Detail + "). Ative 'Otimizar MTU' nas Opções.";
                        }
                        else if (tunnelRunning && !hsOk)
                        {
                            vColor = "Warn";
                            vTitle = "Servidor VPN instável ou sem resposta";
                            vSub = "O WireSock abriu, mas o servidor não respondeu ao handshake." +
                                   (string.IsNullOrEmpty(logHint) ? "" : " " + logHint);
                        }
                        else if (allNetDown && tunnelRunning && !endpointDown)
                        {
                            vColor = "Danger";
                            vTitle = "Servidor VPN instável ou sem resposta";
                            vSub = "Sua internet física alcança o servidor, mas o tráfego dentro do túnel não responde. Troque de servidor.";
                        }
                        else if (!dnsRes.Ok)
                        {
                            vColor = "Danger";
                            vTitle = "Falha na resolução DNS do Windows";
                            vSub = "A rede responde, mas nomes não resolvem (" + dnsRes.Detail + ").";
                        }
                        else if (!httpRes.Ok)
                        {
                            vColor = "Warn";
                            vTitle = "Internet instável ou bloqueada";
                            vSub = "DNS respondeu, mas HTTPS falhou (" + httpRes.Detail + ").";
                        }
                        else if (ghostAlert)
                        {
                            vColor = "Warn";
                            vTitle = "Internet OK, mas há rota IPv6 fantasma";
                            vSub = "Uma rota ::/0 residual pode causar travamentos. Clique em \"Reparar internet agora\".";
                        }
                        else if (anyConflictWarn || item4HasErr || item4HasWarn || item5HasErr || item5HasWarn || (!mtuIntentionalCap && mtuRes.Ok && mtuRes.MaxPayload < 1472))
                        {
                            vColor = "Warn";
                            vTitle = tunnelRunning ? "Conexão ativa com avisos" : "Rede OK com avisos";
                            vSub = conflicts.Warnings.Count > 0 ? conflicts.Warnings[0]
                                 : (mtuRes.Ok && mtuRes.MaxPayload < 1472) ? mtuRes.Detail
                                 : item5Problems.Count > 0 ? item5Problems[0]
                                 : d4;
                        }
                        else
                        {
                            vColor = "SecureText";
                            vTitle = "Tudo funcionando normalmente";
                            vSub = (recentNetFlaps > 0 || recentDriverWarns > 0)
                                ? "Sem falhas agora, mas houve oscilações nos últimos 5 min (veja Linha do Tempo ou rode o Teste contínuo)."
                                : tunnelRunning
                                    ? "Internet, UDP, DNS, MTU, driver e servidor VPN aprovados."
                                    : "Internet, UDP, DNS, MTU e servidor acessíveis (VPN desconectada).";
                        }

                        if (!string.IsNullOrEmpty(postFixNote))
                        {
                            if (vColor == "SecureText") vTitle = "Corrigido e verificado";
                            vSub = "Correções aplicadas: " + postFixNote + " — Verificação atual: " + vSub;
                        }

                        SetDiagDot(diagVerdictDot, vColor);
                        txtDiagVerdict.Foreground = vColor == "Danger" ? B("Danger") : vColor == "Warn" ? B("Warn") : B("Text");
                        txtDiagVerdict.Text = vTitle;
                        txtDiagVerdictSub.Text = vSub;

                        var sb = new StringBuilder();
                        sb.AppendLine("VPN Panel v" + Program.Version + " — Diagnóstico (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")");
                        sb.AppendLine("Estado: " + (tunnelRunning ? (hsOk ? "Conectado" : "Conectando") : "Desconectado") +
                                      " | Servidor: " + (cfgSnap != null ? cfgSnap.Name : "nenhum") +
                                      " | Modo: " + effMode);
                        sb.AppendLine("Veredito: " + vTitle + " — " + vSub);
                        sb.AppendLine("1. Internet Base: " + d1);
                        sb.AppendLine("2. DNS do Windows: " + d2);
                        sb.AppendLine("3. Túnel & Servidor VPN: " + d3);
                        sb.AppendLine("4. Kill Switch & IPv6: " + d4);
                        sb.AppendLine("5. Configuração & Apps: " + d5);
                        sb.AppendLine("6. MTU & Pacotes de Jogos: " + d6);
                        sb.AppendLine("7. Conflitos & Driver: " + d7);
                        lastDiagSummary = sb.ToString().TrimEnd();
                        UpdateDiagTimeline();
                    }));
                }
                finally
                {
                    if (!uiPosted && !exiting)
                    {
                        var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                        if (ui != null)
                        {
                            ui.BeginInvoke(new Action(() =>
                            {
                                if (exiting || myEpoch != diagEpoch) return;
                                diagQuickRunning = false;
                                if (btnDiagQuick != null)
                                {
                                    btnDiagQuick.IsEnabled = !diagRepairRunning;
                                    btnDiagQuick.Content = "Testar agora";
                                }
                            }));
                        }
                    }
                    try { myCts.Dispose(); } catch { }
                }
            });
        }

        void ToggleContinuousDiag()
        {
            if (diagContinuousRunning)
            {
                StopContinuousDiag("Teste contínuo parado pelo usuário");
                return;
            }
            if (exiting || pageDiag == null) return;

            if (diagContCts != null)
            {
                try { diagContCts.Cancel(); } catch { }
            }
            CancellationTokenSource myCts = new CancellationTokenSource();
            diagContCts = myCts;
            CancellationToken ct = myCts.Token;
            int myContEpoch = ++diagContEpoch;

            diagContinuousRunning = true;
            lastContSummary = "";
            btnDiagContinuous.Content = "Parar teste";
            txtDiagLiveStatus.Text = "Executando rajada UDP (0/60s)";
            txtDiagLiveTime.Text = "00:00";
            txtDiagSentLoss.Text = "0 / 0%";
            txtDiagPing.Text = "— / —";
            txtDiagJitter.Text = "—";
            txtDiagDrops.Text = "0 / 0";
            txtDiagGw.Text = "—";
            txtDiagEp.Text = "—";

            ConfigInfo cfgSnap = active;
            AppMode effMode = EffectiveAppMode();
            bool tunnelRunning = wasRunning == true;

            diagContThread = new Thread(() =>
            {
                try
                {
                    int udpSent = 0, udpLost = 0;
                    int microDrops = 0, drops = 0, spikes200 = 0;
                    int consecDownSecs = 0;
                    int gwSent = 0, gwLost = 0;
                    long gwLatSum = 0;
                    int gwOkCount = 0;
                    bool gwFiltered = false;
                    int epOkCount = 0;
                    bool epFiltered = false;
                    int epLastMs = -1;
                    bool epLastOk = false;
                    double rfcJitter = 0;
                    int prevRtt = -1;
                    var latencies = new List<int>(300);
                    long lastEventLogTick = 0;

                    IPAddress gwIp = NetDiag.GetPhysicalGateway();
                    IPAddress epIp = null;
                    if (cfgSnap != null && !string.IsNullOrEmpty(cfgSnap.Endpoint))
                    {
                        if (!IPAddress.TryParse(cfgSnap.Endpoint, out epIp))
                        {
                            try
                            {
                                IPAddress[] addrs = Dns.GetHostAddresses(cfgSnap.Endpoint);
                                if (addrs != null && addrs.Length > 0) epIp = addrs[0];
                            }
                            catch { }
                        }
                    }

                    using (var ping = new System.Net.NetworkInformation.Ping())
                    {
                        Stopwatch totalSw = Stopwatch.StartNew();
                        int iter = 0;
                        while (!ct.IsCancellationRequested && totalSw.ElapsedMilliseconds < 60000)
                        {
                            iter++;
                            Stopwatch secSw = Stopwatch.StartNew();

                            if (gwIp == null && iter % 10 == 0) gwIp = NetDiag.GetPhysicalGateway();

                            // 5x UDP DNS probes (~200ms cadence) -> catches sub-second game microdrops
                            int burstOk = 0, burstLost = 0;
                            bool burstSpike250 = false;
                            for (int b = 0; b < 5; b++)
                            {
                                if (ct.IsCancellationRequested || totalSw.ElapsedMilliseconds >= 60000) break;
                                NetDiag.ProbeResult u = NetDiag.ProbeUdpDns("1.1.1.1", 53, 350, ct);
                                if (ct.IsCancellationRequested) break;
                                udpSent++;
                                if (u.Ok)
                                {
                                    burstOk++;
                                    latencies.Add(u.LatencyMs);
                                    if (u.LatencyMs > 200) spikes200++;
                                    if (u.LatencyMs > 250) burstSpike250 = true;
                                    if (prevRtt >= 0)
                                    {
                                        int diff = Math.Abs(u.LatencyMs - prevRtt);
                                        rfcJitter += (diff - rfcJitter) / 16.0;
                                    }
                                    prevRtt = u.LatencyMs;
                                }
                                else
                                {
                                    burstLost++;
                                    udpLost++;
                                }

                                if (b < 4)
                                {
                                    int nextSlotMs = (b + 1) * 200 - (int)secSw.ElapsedMilliseconds;
                                    int remMs = 60000 - (int)totalSw.ElapsedMilliseconds;
                                    int slotWaitMs = Math.Min(nextSlotMs, remMs);
                                    if (slotWaitMs > 0)
                                    {
                                        try { ct.WaitHandle.WaitOne(slotWaitMs); }
                                        catch { break; }
                                    }
                                }
                            }
                            if (ct.IsCancellationRequested) break;
                            if (burstOk + burstLost == 0) break;

                            // Physical gateway ICMP check (separates Wi-Fi/router drops from VPN/driver)
                            bool gwOkNow = false;
                            if (gwIp != null && !gwFiltered && totalSw.ElapsedMilliseconds < 60000)
                            {
                                gwSent++;
                                try
                                {
                                    var rep = ping.Send(gwIp, 350);
                                    if (rep != null && rep.Status == System.Net.NetworkInformation.IPStatus.Success)
                                    {
                                        gwOkNow = true;
                                        gwOkCount++;
                                        gwLatSum += Math.Max(1, (int)rep.RoundtripTime);
                                    }
                                    else gwLost++;
                                }
                                catch { gwLost++; }
                                if (!gwOkNow && gwSent >= 5 && gwOkCount == 0 && udpSent > udpLost)
                                    gwFiltered = true;
                            }

                            // VPN Endpoint check (marks "n/d" if filtered during the first iterations)
                            if (epIp != null && !epFiltered && totalSw.ElapsedMilliseconds < 60000)
                            {
                                bool gotEp = false;
                                try
                                {
                                    var epRep = ping.Send(epIp, 350);
                                    if (epRep != null && epRep.Status == System.Net.NetworkInformation.IPStatus.Success)
                                    {
                                        gotEp = true;
                                        epOkCount++;
                                        epLastOk = true;
                                        epLastMs = Math.Max(1, (int)epRep.RoundtripTime);
                                    }
                                }
                                catch { }

                                if (!gotEp)
                                {
                                    epLastOk = false;
                                    if (iter == 5 && epOkCount == 0 && cfgSnap != null)
                                    {
                                        NetDiag.ProbeResult tcpEp = NetDiag.ProbeEndpoint(cfgSnap, 800, ct);
                                        if (tcpEp != null && tcpEp.Ok)
                                        {
                                            epOkCount++;
                                            epLastOk = true;
                                            epLastMs = tcpEp.LatencyMs;
                                        }
                                        else if (gwIp == null || gwOkNow || (effMode == AppMode.All && tunnelRunning))
                                        {
                                            epFiltered = true;
                                        }
                                    }
                                }
                            }

                            if (ct.IsCancellationRequested) break;

                            bool fullSecDown = burstOk == 0;
                            if (fullSecDown)
                            {
                                consecDownSecs++;
                                if (consecDownSecs == 2) drops++;
                            }
                            else
                            {
                                if (consecDownSecs == 1) microDrops++; // brief 1s outage recovered
                                consecDownSecs = 0;
                                if (burstLost > 0 || burstSpike250) microDrops++;
                            }

                            bool gwBlockedInAll = effMode == AppMode.All && tunnelRunning;
                            if ((burstLost > 0 || fullSecDown) && (lastEventLogTick == 0 || ElapsedSeconds(lastEventLogTick) >= 5))
                            {
                                lastEventLogTick = NowTicks();
                                string reason;
                                if (gwIp != null && !gwFiltered && !gwOkNow && gwOkCount > 0 && !epLastOk && !gwBlockedInAll)
                                    reason = "Perda UDP + gateway físico falhou -> oscilação na rede local/Wi-Fi";
                                else if (epIp != null && !epFiltered && !epLastOk)
                                    reason = "Perda UDP + endpoint falhou (gateway OK) -> falha na internet externa ou servidor VPN";
                                else if (effMode == AppMode.Only && tunnelRunning)
                                    reason = "Microqueda UDP (" + burstLost + "/5 perdidos; gateway OK) -> perda no bypass do driver WireSock";
                                else
                                    reason = "Microqueda UDP (" + burstLost + "/5 perdidos; " + (gwBlockedInAll ? "túnel All" : "gateway OK") + ") -> perda no túnel/rota";
                                NetMon.Record("Teste", fullSecDown ? NetMon.EventKind.Error : NetMon.EventKind.Warn, reason);
                            }

                            int avgMs = 0, maxMs = 0;
                            if (latencies.Count > 0)
                            {
                                long sum = 0;
                                for (int i = 0; i < latencies.Count; i++)
                                {
                                    int val = latencies[i];
                                    sum += val;
                                    if (val > maxMs) maxMs = val;
                                }
                                avgMs = (int)Math.Round((double)sum / latencies.Count);
                            }

                            int curSec = Math.Min(60, Math.Max(1, (int)Math.Round(totalSw.Elapsed.TotalSeconds)));
                            int curSent = udpSent, curLost = udpLost;
                            int curMicro = microDrops, curDrops = drops, curSpikes = spikes200;
                            int latCount = latencies.Count, curAvg = avgMs, curMax = maxMs;
                            int curJitter = (int)Math.Round(rfcJitter);
                            string gwText = gwIp == null ? "indisp."
                                          : gwOkCount > 0 ? ((int)Math.Round((double)gwLatSum / gwOkCount) + "ms (" + gwLost + "p)")
                                          : gwBlockedInAll ? "filtrado (túnel)"
                                          : gwFiltered ? "n/d (ICMP)"
                                          : ("falhou (" + gwLost + "/" + gwSent + ")");
                            string epText = epIp == null ? "—"
                                          : epFiltered ? "n/d (ICMP)"
                                          : epLastOk ? (epLastMs + " ms")
                                          : "sem resp.";

                            bool iterCancelled = ct.IsCancellationRequested;
                            var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                            if (ui != null)
                            {
                                ui.BeginInvoke(new Action(() =>
                                {
                                    if (exiting || myContEpoch != diagContEpoch || iterCancelled) return;
                                    txtDiagLiveTime.Text = string.Format("{0:00}:{1:00}", curSec / 60, curSec % 60);
                                    txtDiagLiveStatus.Text = "Rajada UDP 5 pps (" + curSec + "/60s)";
                                    int lossPct = curSent > 0 ? (int)Math.Round(100.0 * curLost / curSent) : 0;
                                    txtDiagSentLoss.Text = curSent + " / " + lossPct + "%";
                                    txtDiagPing.Text = latCount > 0 ? (curAvg + " / " + curMax + " ms") : "— / —";
                                    txtDiagJitter.Text = latCount >= 2 ? (curJitter + " ms / " + curSpikes) : (latCount == 1 ? "0 ms / " + curSpikes : "—");
                                    txtDiagDrops.Text = curMicro + " / " + curDrops;
                                    txtDiagGw.Text = gwText;
                                    txtDiagEp.Text = epText;
                                    if (WindowShowing && pageDiag.Visibility == Visibility.Visible) UpdateDiagTimeline();
                                }));
                            }

                            int waitMs = Math.Min(1000 - (int)secSw.ElapsedMilliseconds, 60000 - (int)totalSw.ElapsedMilliseconds);
                            if (waitMs > 0)
                            {
                                try { ct.WaitHandle.WaitOne(waitMs); }
                                catch { break; }
                            }
                        }
                    }

                    if (consecDownSecs == 1) microDrops++;

                    bool doneCancelled = ct.IsCancellationRequested;
                    var uiDone = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (uiDone != null)
                    {
                        uiDone.BeginInvoke(new Action(() =>
                        {
                            if (exiting || myContEpoch != diagContEpoch) return;
                            diagContinuousRunning = false;
                            btnDiagContinuous.Content = "Teste contínuo";
                            if (!doneCancelled)
                            {
                                string statusText = (drops == 0 && microDrops == 0 && udpLost == 0)
                                    ? "Concluído — estável (0% perda)"
                                    : "Concluído — " + microDrops + " micro, " + drops + " queda(s)";
                                txtDiagLiveStatus.Text = statusText;
                                txtDiagDrops.Text = microDrops + " / " + drops;
                                lastContSummary = "Teste contínuo (" + statusText + "): " +
                                                  "UDP=" + txtDiagSentLoss.Text +
                                                  " | Ping(méd/máx)=" + txtDiagPing.Text +
                                                  " | Jitter/Picos=" + txtDiagJitter.Text +
                                                  " | Micro/Quedas=" + txtDiagDrops.Text +
                                                  " | GW=" + txtDiagGw.Text +
                                                  " | Endpoint=" + txtDiagEp.Text;
                                UpdateDiagTimeline();
                            }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    NetMon.Record("Teste", NetMon.EventKind.Error, "Erro inesperado no teste contínuo: " + ex.Message);
                    var uiErr = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (uiErr != null)
                    {
                        uiErr.BeginInvoke(new Action(() =>
                        {
                            if (exiting || myContEpoch != diagContEpoch) return;
                            diagContinuousRunning = false;
                            if (btnDiagContinuous != null) btnDiagContinuous.Content = "Teste contínuo";
                            if (txtDiagLiveStatus != null) txtDiagLiveStatus.Text = "Interrompido por erro";
                        }));
                    }
                }
                finally
                {
                    try { myCts.Dispose(); } catch { }
                }
            }) { IsBackground = true, Name = "DiagContinuous" };
            diagContThread.Start();
        }

        void StopContinuousDiag(string statusText)
        {
            diagContEpoch++;
            if (diagContCts != null)
            {
                try { diagContCts.Cancel(); } catch { }
                diagContCts = null;
            }
            if (!diagContinuousRunning) return;
            diagContinuousRunning = false;
            if (btnDiagContinuous != null) btnDiagContinuous.Content = "Teste contínuo";
            if (txtDiagLiveStatus != null && !string.IsNullOrEmpty(statusText)) txtDiagLiveStatus.Text = statusText;
        }

        void RepairDiagNetwork()
        {
            if (exiting) return;
            StopContinuousDiag("Interrompido para reparo");
            pendingPostHealDiagNote = null;

            NetDiag.MtuResult mtuSnap = lastDiagMtu;
            NetDiag.ConflictReport confSnap = lastDiagConflicts;
            bool dnsFailedSnap = lastDiagDnsFailed;
            bool logMtuSnap = lastDiagLogMtuErr;

            bool stopExternal = false;
            List<string> extSvcs = confSnap != null ? confSnap.ConflictingServices : null;
            if (extSvcs != null && extSvcs.Count > 0)
            {
                bool onlyExtSvcs = lastDiagFixableCount > 0 && lastDiagFixableCount <= extSvcs.Count;
                if (onlyExtSvcs)
                {
                    if (!Dialogs.Confirm(w, "Encerrar serviços conflitantes?",
                        "Encontramos estes serviços em execução que causam quedas em jogos/VPN:\n\n\u2022 " +
                        string.Join("\n\u2022 ", extSvcs.ToArray()) +
                        "\n\nDeseja tentar encerrá-los temporariamente agora?",
                        "Encerrar serviços", true))
                        return;
                    stopExternal = true;
                }
                else
                {
                    stopExternal = Dialogs.Confirm(w, "Incluir serviços conflitantes?",
                        "Além dos ajustes de rede/MTU/WireSock, estes serviços estão em execução:\n\n\u2022 " +
                        string.Join("\n\u2022 ", extSvcs.ToArray()) +
                        "\n\nClique em \u201cIncluir serviços\u201d para encerrá-los junto, ou \u201cCancelar\u201d para corrigir apenas os demais problemas.",
                        "Incluir serviços", true);
                }
            }

            if (connecting || reconnectPending || (reconnectTimer != null && reconnectTimer.IsEnabled))
            {
                bool ok = Dialogs.Confirm(w, "Conexão em andamento",
                    "Para limpar o bloqueio de rede e rotas com segurança, o VPN Panel precisa encerrar a tentativa atual primeiro.",
                    "Encerrar e corrigir", true);
                if (!ok) return;
                CancelQuickDiag(true);
                diagRepairRunning = true;
                btnDiagRepair.IsEnabled = false;
                btnDiagRepair.Content = "Corrigindo...";
                SetDiagDot(diagVerdictDot, "Muted");
                txtDiagVerdict.Foreground = B("Text");
                txtDiagVerdict.Text = "Desconectando para corrigir...";
                txtDiagVerdictSub.Text = "Encerrando tentativa de túnel antes de aplicar as correções...";
                DisconnectAndResetLock(() => ExecuteAutoHealer(mtuSnap, confSnap, dnsFailedSnap, logMtuSnap, false, stopExternal));
                return;
            }

            ExecuteAutoHealer(mtuSnap, confSnap, dnsFailedSnap, logMtuSnap, wasRunning == true, stopExternal);
        }

        void ExecuteAutoHealer(NetDiag.MtuResult mtuSnap, NetDiag.ConflictReport confSnap, bool dnsFailedSnap, bool logMtuSnap, bool tunnelRunningNow, bool stopExternal)
        {
            if (exiting) return;
            CancelQuickDiag(true);
            diagRepairRunning = true;
            btnDiagRepair.IsEnabled = false;
            btnDiagRepair.Content = "Corrigindo...";
            SetDiagDot(diagVerdictDot, "Muted");
            txtDiagVerdict.Foreground = B("Text");
            txtDiagVerdict.Text = "Aplicando correções...";
            txtDiagVerdictSub.Text = "Limpando bloqueios/rotas, verificando serviços e ajustando MTU...";
            NetMon.Record("Autocura", NetMon.EventKind.Info, "Correção manual executada na aba Testes");

            bool optMtuSnap = settings.OptimizeMtu;
            int curMaxMtuSnap = DiagEffectiveMtu(tunnelRunningNow, EffectiveAppMode(), settings);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AutoHealer.FixReport report = null;
                try
                {
                    report = AutoHealer.ApplyFixes(
                        optMtuSnap,
                        curMaxMtuSnap,
                        mtuSnap,
                        confSnap,
                        logMtuSnap,
                        dnsFailedSnap,
                        tunnelRunningNow,
                        stopExternal,
                        () => exiting || Tunnel.Connecting || reconnectPending || (!tunnelRunningNow && wasRunning == true));
                }
                catch (Exception ex)
                {
                    report = new AutoHealer.FixReport();
                    report.Failed.Add(ex.Message);
                }
                var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                if (ui == null) return;
                ui.BeginInvoke(new Action(() =>
                {
                    if (exiting) return;
                    diagRepairRunning = false;
                    if (btnDiagQuick != null) btnDiagQuick.IsEnabled = true;
                    btnDiagRepair.IsEnabled = true;
                    btnDiagRepair.Content = "Corrigir problemas encontrados";
                    ApplyHealReportToSettings(report);
                    if (report != null && report.SuggestedMtu > 0 && tunnelRunningNow)
                        bgMtuReducedThisSession = true;
                    CheckIp();

                    var parts = new List<string>();
                    if (report != null && report.Applied.Count > 0) parts.Add(string.Join("; ", report.Applied.ToArray()));
                    if (report != null && report.Failed.Count > 0) parts.Add("Falhas: " + string.Join("; ", report.Failed.ToArray()));
                    string fixNote = parts.Count > 0 ? string.Join(" | ", parts.ToArray()) : "Nenhuma alteração extra necessária";

                    SetDiagDot(diagVerdictDot, (report != null && report.Failed.Count > 0) ? "Warn" : "SecureText");
                    txtDiagVerdict.Foreground = (report != null && report.Failed.Count > 0) ? B("Warn") : B("Text");
                    txtDiagVerdict.Text = (report != null && report.FixedCount > 0) ? "Correções aplicadas (" + report.FixedCount + ")" : "Verificação de reparo concluída";
                    txtDiagVerdictSub.Text = fixNote;

                    if (report != null && report.NeedReconnect && wasRunning == true)
                    {
                        string dlgDetail = report.Applied.Count > 0
                            ? "Correções aplicadas:\n\u2022 " + string.Join("\n\u2022 ", report.Applied.ToArray())
                            : "Ajustes preparados.";
                        bool rec = Dialogs.Confirm(w, "Reconexão necessária",
                            dlgDetail + "\n\nDeseja reconectar a VPN agora para aplicar o filtro/MTU (" +
                            (EffectiveMaxMtu() > 0 ? EffectiveMaxMtu() + " B" : "padrão") + ")?",
                            "Reconectar agora", false);
                        if (rec)
                        {
                            pendingPostHealDiagNote = fixNote;
                            txtDiagVerdict.Text = "Reconectando para aplicar...";
                            txtDiagVerdictSub.Text = fixNote;
                            Reconnect();
                            return;
                        }
                    }

                    RunQuickDiag(fixNote);
                }));
            });
        }

        void CopyDiagReport()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(lastDiagSummary))
            {
                sb.AppendLine(lastDiagSummary);
            }
            else
            {
                sb.AppendLine("VPN Panel v" + Program.Version + " — Diagnóstico");
                sb.AppendLine("Veredito: " + (txtDiagVerdict != null ? txtDiagVerdict.Text : ""));
                if (txtChk1Detail != null) sb.AppendLine("1. Internet Base: " + txtChk1Detail.Text);
                if (txtChk2Detail != null) sb.AppendLine("2. DNS do Windows: " + txtChk2Detail.Text);
                if (txtChk3Detail != null) sb.AppendLine("3. Túnel & Servidor VPN: " + txtChk3Detail.Text);
                if (txtChk4Detail != null) sb.AppendLine("4. Kill Switch & IPv6: " + txtChk4Detail.Text);
                if (txtChk5Detail != null) sb.AppendLine("5. Configuração & Apps: " + txtChk5Detail.Text);
                if (txtChk6Detail != null) sb.AppendLine("6. MTU & Pacotes de Jogos: " + txtChk6Detail.Text);
                if (txtChk7Detail != null) sb.AppendLine("7. Conflitos & Driver: " + txtChk7Detail.Text);
            }
            if (!string.IsNullOrEmpty(lastContSummary))
            {
                sb.AppendLine(lastContSummary);
            }
            else if (txtDiagSentLoss != null && txtDiagSentLoss.Text != "—" && txtDiagSentLoss.Text != "0 / 0%")
            {
                sb.AppendLine("Teste contínuo (" + txtDiagLiveStatus.Text + "): " +
                              "UDP=" + txtDiagSentLoss.Text +
                              " | Ping(méd/máx)=" + txtDiagPing.Text +
                              " | Jitter/Picos=" + txtDiagJitter.Text +
                              " | Micro/Quedas=" + txtDiagDrops.Text +
                              " | GW=" + txtDiagGw.Text +
                              " | Endpoint=" + txtDiagEp.Text);
            }
            string timeline = NetMon.ExportText(30);
            if (!string.IsNullOrEmpty(timeline))
            {
                sb.AppendLine();
                sb.AppendLine("Linha do Tempo (últimos 30 eventos):");
                sb.AppendLine(timeline);
            }

            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
                btnDiagCopy.Content = "Copiado!";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
                t.Tick += (s, e) => { t.Stop(); if (!exiting && btnDiagCopy != null) btnDiagCopy.Content = "Copiar resultado"; };
                t.Start();
            }
            catch { }
        }

        class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? SD.Color.FromArgb(238, 241, 250) : SD.Color.FromArgb(142, 155, 192);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(WF.ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = SD.Color.FromArgb(142, 155, 192);
                base.OnRenderArrow(e);
            }

            // the stock check mark is a black bitmap, invisible on the dark menu: draw an accent one
            protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
            {
                SD.Rectangle r = e.ImageRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                SD.Graphics g = e.Graphics;
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                float x = r.Left + r.Width / 2F, y = r.Top + r.Height / 2F, u = Math.Min(r.Width, r.Height) / 16F;
                using (var pen = new SD.Pen(activeGdiAccent, 2F * u))
                    g.DrawLines(pen, new[] { new SD.PointF(x - 5 * u, y), new SD.PointF(x - 1.5F * u, y + 3.5F * u), new SD.PointF(x + 5 * u, y - 4 * u) });
                g.SmoothingMode = smoothing;
            }
        }

        class DarkMenuColors : WF.ProfessionalColorTable
        {
            static readonly SD.Color Bg = SD.Color.FromArgb(16, 28, 44);
            static readonly SD.Color Hover = SD.Color.FromArgb(38, 59, 86);
            static readonly SD.Color Line = SD.Color.FromArgb(34, 54, 79);
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
