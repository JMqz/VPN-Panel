// Main window (MainWindow.xaml) and tray icon: fields, native interop, constructor and startup.
// The rest of this partial class is split by area in MainWindow.*.cs.

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
        const int MaxReconnects = 4;
        // first retry almost at once: in split tunneling the apps fall back to the real IP while the tunnel is down
        static readonly int[] ReconnectDelaysSec = new[] { 1, 5, 12, 20 };

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
        volatile int connectSeq;
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
        readonly CheckBox chkReconnect, chkLock, chkMtu, chkGameMode, chkAutoHeal, chkHotkey, chkStartup, chkAuto, chkSkipUac, chkUpdates;
        readonly RadioButton segDnsDefault, segDnsCloudflare, segDnsQuad9, segEngineAuto, segEngineWireSock;
        readonly TextBlock txtEngineStatus;
        readonly Button btnInstallWireGuard;
        bool installingWireGuard, wintunPollRunning;
        long lastWintunLogPollTick;
        readonly object mtuCacheLock = new object();
        string cachedMtuNetSig;
        bool hasCachedPreMtu;
        int cachedPreMtu;
        DateTime cachedPreMtuAtUtc = DateTime.MinValue;
        readonly TextBlock txtLockNote;
        readonly Border cardUpdate;
        readonly Button btnApplyUpdate, btnCheckUpdate, btnResetLock, btnCleanLegacy;
        bool cleanLegacyRunning;
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
        bool userStopped, connecting, reconnectPending, scheduleReconnectAfterStop, pendingPreConnectCleanup;
        bool waitingForNetworkRestore, reconnectProbeRunning;
        int reconnectAttempts, postDropCleanupSeq, reconnSeq;
        long waitingForNetSinceTick, lastDualNicWarnTick;
        readonly HashSet<string> failedServersThisDrop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint msgTaskbarCreated;
        IntPtr windowHandle, hTaskbarBigIcon, hTaskbarSmallIcon;
        string lastTaskbarAccentId;
        bool hotkeyRegistered;

        const int HotkeyId = 9001;
        const uint ModAlt = 0x0001;
        const uint ModControl = 0x0002;
        const uint VkV = 0x56;
        const int WmHotkey = 0x0312;
        const int WmSetIcon = 0x0080;
        const int IconSmall = 0;
        const int IconBig = 1;
        const int GclpHicon = -14;
        const int GclpHiconSm = -34;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
        static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propStore);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pv);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string lpString);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
            public PropertyKey(Guid g, uint id) { fmtid = g; pid = id; }
        }

        [StructLayout(LayoutKind.Explicit)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public IntPtr ptrVal;
            [FieldOffset(16)] public long pad;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        class ComTaskbarList { }

        static readonly Guid IidPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        static readonly Guid PkeyFmtAppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        static readonly PropertyKey PkeyAppUserModelId = new PropertyKey(PkeyFmtAppUserModel, 5);
        static readonly PropertyKey PkeyAppUserModelRelaunchCmd = new PropertyKey(PkeyFmtAppUserModel, 2);
        static readonly PropertyKey PkeyAppUserModelRelaunchIcon = new PropertyKey(PkeyFmtAppUserModel, 3);
        static readonly PropertyKey PkeyAppUserModelRelaunchName = new PropertyKey(PkeyFmtAppUserModel, 4);
        DateTime startedAt, lastProcScan, lastHomePing, lastHomeAppsCheck;
        long connectTotalStartTick, connectLaunchTick, resumeGraceUntilTick, lastDropTick;
        long rehandshakeSuspectTick;
        int rehandshakeGen;
        bool connectPausedForDialog, activeProbeRunning, rehandshakeConfirmedDown, rehandshakeOnlyChecked10s;

        // active probing through the tunnel (see RunLivenessProbe): the WireSock log is block-buffered,
        // so a dead server can take minutes to show up there
        const int LivenessEverySec = 10, LivenessRetrySec = 5, LivenessFailLimit = 3;
        NetDiag.TunnelUdpSession livenessSession;
        bool livenessRunning, livenessArmed;
        int livenessFails, livenessGen, graceProbeFails;
        long lastLivenessTick;
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
            chkGameMode = E<CheckBox>("ChkGameMode");
            segDnsDefault = E<RadioButton>("SegDnsDefault");
            segDnsCloudflare = E<RadioButton>("SegDnsCloudflare");
            segDnsQuad9 = E<RadioButton>("SegDnsQuad9");
            segEngineAuto = E<RadioButton>("SegEngineAuto");
            segEngineWireSock = E<RadioButton>("SegEngineWireSock");
            txtEngineStatus = E<TextBlock>("TxtEngineStatus");
            btnInstallWireGuard = E<Button>("BtnInstallWireGuard");
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
            btnCleanLegacy = E<Button>("BtnCleanLegacy");
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
                if (activePreset != null) ApplyTaskbarIcon(activePreset, null, false);
                SyncHotkey();
            };
            w.IsVisibleChanged += (s, e) => OnVisibilityChanged();
            w.StateChanged += (s, e) => OnVisibilityChanged();
            Application.Current.SessionEnding += (s, e) =>
            {
                try
                {
                    if (Tunnel.Active(true) || Tunnel.Connecting || Tunnel.HasPendingNetworkLock)
                    {
                        NetMon.Record("Sistema", NetMon.EventKind.Info, "Encerrando túnel antes do desligamento do Windows");
                        Tunnel.StopForShutdown(settings.NetworkLock);
                    }
                }
                catch { }
                Quit();
            };

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
            E<Button>("BtnPresetDiscord").Click += (s, e) => ApplyDiscordPreset();
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
            if (chkGameMode != null) chkGameMode.IsChecked = settings.GameStabilityMode;
            if (segDnsDefault != null)
            {
                if (settings.CustomDns == "1.1.1.1" && segDnsCloudflare != null) segDnsCloudflare.IsChecked = true;
                else if (settings.CustomDns == "9.9.9.9" && segDnsQuad9 != null) segDnsQuad9.IsChecked = true;
                else segDnsDefault.IsChecked = true;
            }
            if (segEngineAuto != null && segEngineWireSock != null)
            {
                if (settings.EnginePreference == VpnEnginePreference.WireSock) segEngineWireSock.IsChecked = true;
                else segEngineAuto.IsChecked = true;
            }
            UpdateEngineUi();
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
            if (chkGameMode != null) onToggle(chkGameMode, () => { SaveSettings(); NotifyOptionNeedsReconnect(); });
            RoutedEventHandler onDnsChanged = (s, e) =>
            {
                if (loading) return;
                settings.CustomDns = segDnsCloudflare != null && segDnsCloudflare.IsChecked == true ? "1.1.1.1"
                                   : segDnsQuad9 != null && segDnsQuad9.IsChecked == true ? "9.9.9.9"
                                   : "";
                SaveSettings();
                NotifyOptionNeedsReconnect();
            };
            if (segDnsDefault != null) segDnsDefault.Checked += onDnsChanged;
            if (segDnsCloudflare != null) segDnsCloudflare.Checked += onDnsChanged;
            if (segDnsQuad9 != null) segDnsQuad9.Checked += onDnsChanged;
            RoutedEventHandler onEngineChanged = (s, e) =>
            {
                if (loading) return;
                settings.EnginePreference = segEngineWireSock != null && segEngineWireSock.IsChecked == true
                    ? VpnEnginePreference.WireSock
                    : VpnEnginePreference.Auto;
                SaveSettings();
                UpdateEngineUi();
                NotifyOptionNeedsReconnect();
            };
            if (segEngineAuto != null) segEngineAuto.Checked += onEngineChanged;
            if (segEngineWireSock != null) segEngineWireSock.Checked += onEngineChanged;
            if (btnInstallWireGuard != null) btnInstallWireGuard.Click += (s, e) => InstallWireGuard();
            onToggle(chkAuto, SaveSettings);
            onToggle(chkSkipUac, () => { SaveSettings(); SyncTasks(); });
            onToggle(chkHotkey, () => { SaveSettings(); SyncHotkey(); });
            onToggle(chkUpdates, SaveSettings);
            onToggle(chkStartup, ToggleStartup);
            onToggle(chkSharedApps, ToggleSharedApps);
            btnCheckUpdate.Click += (s, e) => CheckForUpdates(true);
            btnApplyUpdate.Click += (s, e) => InstallUpdate();
            btnResetLock.Click += (s, e) => ResetNetworkLockAction();
            if (btnCleanLegacy != null) btnCleanLegacy.Click += (s, e) => CleanLegacyAction();
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
            menu.Items.Add("Restaurar internet padrão", null, (s, e) => ResetNetworkLockAction());
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
            reconnectTimer.Tick += (s, e) => { reconnectTimer.Stop(); ExecuteReconnectTimerTick(); };
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
            var startupHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            startupHealthTimer.Tick += (s, e) => { startupHealthTimer.Stop(); RollbackManager.MarkStartupHealthy(); };
            startupHealthTimer.Start();

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
                            livenessFails = 0;
                            lastLivenessTick = NowTicks();
                            if (connectLaunchTick > 0) connectLaunchTick = NowTicks();
                            if (isResume)
                            {
                                resumeGraceUntilTick = FutureTicks(18.0);
                                Latency.RouteChanged();
                                ThreadPool.QueueUserWorkItem(_ => ConfFile.FlushDnsCache());
                                ipTimer.Stop();
                                ipTimer.Start();
                                if (settings.AutoHeal && wasRunning != true && !connecting && Tunnel.HasPendingNetworkLock)
                                    QueueBackgroundAutoHeal(false, false);
                                if (settings.AutoHeal)
                                    VerifyPostResumeInternetAsync(settings.NetworkLock);
                                if (waitingForNetworkRestore)
                                    WakeAutoReconnectOnNetworkReturn();
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
                        if (waitingForNetworkRestore) WakeAutoReconnectOnNetworkReturn();
                    }));
                }
            };
            networkAvailHandler = (s, e) =>
            {
                bool avail = e != null && e.IsAvailable;
                NetMon.Record("Rede", avail ? NetMon.EventKind.Info : NetMon.EventKind.Error,
                    avail ? "Rede do Windows ficou disponível" : "Rede do Windows ficou indisponível (sem link)");
                if (avail)
                {
                    var ui = Application.Current != null ? Application.Current.Dispatcher : null;
                    if (ui != null)
                    {
                        ui.BeginInvoke(new Action(() =>
                        {
                            if (!exiting && waitingForNetworkRestore) WakeAutoReconnectOnNetworkReturn();
                        }));
                    }
                }
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
                if (activePreset != null)
                {
                    lastTaskbarAccentId = null;
                    ApplyTaskbarIcon(activePreset, null, false);
                }
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
            RollbackManager.MarkStartupHealthy();
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
            ResetLiveness(true);
            CloseLogStream();
            Geo.Flush();
            if (showWaitReg != null) { try { showWaitReg.Unregister(null); } catch { } }
            if (showEvent != null) { try { showEvent.Dispose(); } catch { } }
            tray.Visible = false;
            tray.Dispose();
            if (trayOn != null) trayOn.Dispose();
            if (trayOff != null) trayOff.Dispose();
            if (trayWarn != null) trayWarn.Dispose();
            if (hTaskbarBigIcon != IntPtr.Zero) { DestroyIcon(hTaskbarBigIcon); hTaskbarBigIcon = IntPtr.Zero; }
            if (hTaskbarSmallIcon != IntPtr.Zero) { DestroyIcon(hTaskbarSmallIcon); hTaskbarSmallIcon = IntPtr.Zero; }
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
    }
}
