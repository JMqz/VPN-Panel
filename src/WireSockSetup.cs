// Installs WireSock (the SDK/CLI package, no UI of its own) without the user leaving the app.
// winget first; when winget is missing or fails, the installer straight from wiresock.net,
// run only if its Authenticode signature is valid and from WireSock's publisher.
// Both the app and the setup run elevated, so the silent install needs no extra UAC prompt.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace VpnPanel
{
    static class SetupUtil
    {
        public static string Winget()
        {
            string alias = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
            return File.Exists(alias) ? alias : "winget.exe";
        }

        public static string Platform()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ??
                          Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            if (arch.Equals("ARM64", StringComparison.OrdinalIgnoreCase)) return "ARM64";
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }

        public static int Run(string exe, string args, int minutes)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(minutes * 60 * 1000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("A instalação demorou demais.");
                }
                return p.ExitCode;
            }
        }

        public static bool SignedBy(string file, string[] publishers)
        {
            if (!TrustedSignature(file)) return false;
            try
            {
                string subject = X509Certificate.CreateFromSignedFile(file).Subject;
                foreach (string name in publishers)
                    if (subject.IndexOf("O=" + name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        // WinVerifyTrust: the signature is intact and chains to a trusted root
        static bool TrustedSignature(string file)
        {
            var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = file };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,     // WTD_UI_NONE
                    dwUnionChoice = 1,  // WTD_CHOICE_FILE
                    pFile = pFile,
                };
                return WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public uint dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);
    }

    static class WireSockSetup
    {
        const string WingetId = "NTKERNEL.WireSockVPNClientCLI";
        const string DownloadUrl = "https://wiresock.net/_api/download-release.php?product=wiresock-secure-connect-sdk&platform={0}&version=latest";
        // Who signs the WireSock installers (the company behind NT Kernel)
        static readonly string[] Publishers = { "The Anti-Cloud Corporation", "NT Kernel" };

        // Returns null when WireSock ends up installed, otherwise a message for the user.
        // status gets short progress lines; it is called from this (background) thread.
        public static string Install(Action<string> status)
        {
            string wingetError = null;
            try
            {
                status("Instalando o WireSock...");
                int code = SetupUtil.Run(SetupUtil.Winget(), "install --id " + WingetId + " --exact --source winget --silent" +
                                          " --accept-package-agreements --accept-source-agreements --disable-interactivity", 10);
                if (Installed()) return null;
                wingetError = "winget terminou com o código 0x" + code.ToString("X8") + ".";
            }
            catch (Exception ex) { wingetError = ex.Message; }

            try
            {
                status("Baixando o WireSock...");
                string dir = Path.Combine(Paths.Data, "temp");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "wiresock-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                using (var web = new WebClient())
                    web.DownloadFile(string.Format(DownloadUrl, SetupUtil.Platform()), file);
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists || fi.Length < 100 * 1024) return "O arquivo do WireSock baixado é inválido ou está incompleto.";
                    using (var lockStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (!SetupUtil.SignedBy(file, Publishers)) return "O arquivo baixado não tem a assinatura do WireSock, então não foi instalado.";
                        status("Instalando o WireSock...");
                        SetupUtil.Run(file, "/S /NCRC", 5);
                    }
                }
                finally { try { File.Delete(file); } catch { } }
                if (Installed()) return null;
                return "A instalação do WireSock não terminou. " + wingetError;
            }
            catch (Exception ex) { return "Não deu para baixar o WireSock: " + ex.Message; }
        }

        static bool Installed()
        {
            Paths.ForgetWireSock();
            return Paths.WireSock != null;
        }
    }

    static class WireGuardSetup
    {
        const string WingetId = "WireGuard.WireGuard";
        const string IndexUrl = "https://download.wireguard.com/windows-client/";
        const string FallbackMsiUrl = "https://download.wireguard.com/windows-client/wireguard-{0}-1.1.1.msi";
        static readonly string[] Publishers = { "WireGuard LLC", "Jason A. Donenfeld" };

        static string ResolveMsiUrl(WebClient web, string archTag)
        {
            try
            {
                string html = web.DownloadString(IndexUrl);
                string bestName = null;
                Version bestVer = null;
                var m = System.Text.RegularExpressions.Regex.Matches(html,
                    "wireguard-" + System.Text.RegularExpressions.Regex.Escape(archTag) + @"-(\d+(?:\.\d+)+)\.msi",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match item in m)
                {
                    Version v;
                    if (Version.TryParse(item.Groups[1].Value, out v) && (bestVer == null || v > bestVer))
                    {
                        bestVer = v;
                        bestName = item.Value;
                    }
                }
                if (bestName != null) return IndexUrl + bestName;
            }
            catch { }
            return string.Format(FallbackMsiUrl, archTag);
        }

        public static string Install(Action<string> status)
        {
            string wingetError = null;
            try
            {
                status("Instalando WireGuard (Wintun)...");
                int code = SetupUtil.Run(SetupUtil.Winget(), "install --id " + WingetId + " --exact --source winget --silent" +
                                          " --accept-package-agreements --accept-source-agreements --disable-interactivity" +
                                          " --custom \"DO_NOT_LAUNCH=1\"", 10);
                if (Installed()) return null;
                wingetError = "winget terminou com o código 0x" + code.ToString("X8") + ".";
            }
            catch (Exception ex) { wingetError = ex.Message; }

            try
            {
                string plat = SetupUtil.Platform();
                string archTag = plat.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64"
                               : plat.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : "amd64";
                status("Baixando WireGuard (Wintun)...");
                string dir = Path.Combine(Paths.Data, "temp");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "wireguard-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".msi");
                using (var web = new WebClient())
                    web.DownloadFile(ResolveMsiUrl(web, archTag), file);
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists || fi.Length < 100 * 1024) return "O instalador do WireGuard baixado é inválido.";
                    using (var lockStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (!SetupUtil.SignedBy(file, Publishers)) return "O instalador baixado não tem assinatura oficial do WireGuard LLC.";
                        status("Instalando driver Wintun...");
                        SetupUtil.Run("msiexec.exe", "/i \"" + file + "\" DO_NOT_LAUNCH=1 /qn /norestart", 5);
                    }
                }
                finally { try { File.Delete(file); } catch { } }
                if (Installed()) return null;
                return "A instalação do WireGuard não concluiu. " + wingetError;
            }
            catch (Exception ex) { return "Não foi possível baixar o WireGuard: " + ex.Message; }
        }

        static bool Installed()
        {
            Paths.ForgetWireGuard();
            return Paths.WireGuard != null;
        }
    }
}
