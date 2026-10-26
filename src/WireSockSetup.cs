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
                int code = Run(Winget(), "install --id " + WingetId + " --exact --source winget --silent" +
                                         " --accept-package-agreements --accept-source-agreements --disable-interactivity", 10);
                if (Installed()) return null;
                wingetError = "winget terminou com o código 0x" + code.ToString("X8") + ".";
            }
            catch (Exception ex) { wingetError = ex.Message; }

            try
            {
                status("Baixando o WireSock...");
                string dir = Path.Combine(Path.GetTempPath(), "VpnPanel-WireSock");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "wiresock-setup.exe");
                using (var web = new WebClient())
                    web.DownloadFile(string.Format(DownloadUrl, Platform()), file);
                try
                {
                    if (!SignedByWireSock(file)) return "O arquivo baixado não tem a assinatura do WireSock, então não foi instalado.";
                    status("Instalando o WireSock...");
                    Run(file, "/S /NCRC", 5);
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

        // The winget alias is per user; it's on PATH, and in WindowsApps when PATH was trimmed
        static string Winget()
        {
            string alias = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
            return File.Exists(alias) ? alias : "winget.exe";
        }

        static string Platform()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ??
                          Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            if (arch.Equals("ARM64", StringComparison.OrdinalIgnoreCase)) return "ARM64";
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }

        static int Run(string exe, string args, int minutes)
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

        static bool SignedByWireSock(string file)
        {
            if (!TrustedSignature(file)) return false;
            try
            {
                string subject = X509Certificate.CreateFromSignedFile(file).Subject;
                foreach (string name in Publishers)
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
}
