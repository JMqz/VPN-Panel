// VPN Panel - ProcUtil: native process helpers (start with inherited log, Ctrl+C shutdown, command lines).

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

namespace VpnPanel
{
    // Fast Win32 process helpers: avoids Process.MainModule exceptions and per-process EnumWindows
    static class ProcUtil
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint SYNCHRONIZE = 0x00100000;
        const uint WAIT_TIMEOUT = 0x00000102;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        const uint PROCESS_TERMINATE = 0x0001;
        const uint WAIT_OBJECT_0 = 0;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_ALL = 0x7; // read | write | delete
        const uint CREATE_ALWAYS = 2;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        const int STARTF_USESTDHANDLES = 0x100;
        const uint CREATE_NO_WINDOW = 0x08000000;
        const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        const int ProcessPowerThrottling = 4;
        const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
        static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
        static readonly IntPtr INVALID_HANDLE_VALUE = (IntPtr)(-1);
        const uint CTRL_C_EVENT = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOEX
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessW(string app, StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr, bool inherit,
            uint flags, IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);

        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr h, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, int processGroupId);

        public static IntPtr OpenForMonitor(int pid)
        {
            return OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        }

        public static DateTime StartTime(IntPtr handle)
        {
            long creation, exit, kernel, user;
            if (handle != IntPtr.Zero && GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                return DateTime.FromFileTime(creation);
            return DateTime.Now;
        }

        // Starts a hidden console program with stdout and stderr going to logPath (truncated), no cmd.exe in between.
        // Only the log handle is inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so the child never holds
        // anything else of the panel's. Returns the process handle (full access); throws Win32Exception.
        public static IntPtr StartLogged(string exe, string cmdLine, string logPath, out int pid)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
            IntPtr log = CreateFileW(logPath, GENERIC_WRITE, FILE_SHARE_ALL, ref sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (log == INVALID_HANDLE_VALUE) log = IntPtr.Zero; // log in use or unwritable: run without one
            IntPtr attrs = IntPtr.Zero, handles = IntPtr.Zero;
            bool attrsReady = false;
            try
            {
                var si = new STARTUPINFOEX { cb = Marshal.SizeOf(typeof(STARTUPINFOEX)) };
                uint flags = CREATE_NO_WINDOW | ABOVE_NORMAL_PRIORITY_CLASS;
                if (log != IntPtr.Zero)
                {
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attrs = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
                    attrsReady = true;
                    handles = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(handles, log);
                    if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception();
                    si.lpAttributeList = attrs;
                    si.dwFlags = STARTF_USESTDHANDLES;
                    si.hStdOutput = si.hStdError = log; // same handle: one file pointer, like 2>&1
                    flags |= EXTENDED_STARTUPINFO_PRESENT;
                }
                PROCESS_INFORMATION pi;
                bool created;
                lock (consoleLock)
                {
                    // The child inherits "ignore Ctrl+C" from us (and we may have inherited it from whoever
                    // launched the panel); clear it so Shutdown can stop the client gracefully later
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                    created = CreateProcessW(exe, new StringBuilder(cmdLine), IntPtr.Zero, IntPtr.Zero, log != IntPtr.Zero,
                        flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi);
                }
                if (!created) throw new Win32Exception();
                CloseHandle(pi.hThread);
                try
                {
                    var pt = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                        StateMask = 0, // disable EcoQoS and timer resolution throttling so UDP packets aren't delayed
                    };
                    SetProcessInformation(pi.hProcess, ProcessPowerThrottling, ref pt, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
                }
                catch { }
                pid = pi.dwProcessId;
                return pi.hProcess;
            }
            finally
            {
                if (attrsReady) DeleteProcThreadAttributeList(attrs);
                if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (log != IntPtr.Zero) CloseHandle(log); // the child keeps its own copy
            }
        }

        static readonly object consoleLock = new object();

        // Ends a console process the way Ctrl+C would, so it can clean up; terminates it after graceMs.
        // Returns true if it exited by itself.
        public static bool Shutdown(int pid, int graceMs)
        {
            IntPtr h = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!IsAlive(h)) return false; // already crashed/exited without graceful Ctrl+C cleanup
                lock (consoleLock)
                {
                    // The panel has no console: borrow the client's for a moment, ignoring the event ourselves
                    // until the client is gone (the ignore flag is inherited, so it can't stay on)
                    if (AttachConsole(pid))
                    {
                        SetConsoleCtrlHandler(IntPtr.Zero, true);
                        bool sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                        FreeConsole();
                        bool exited = sent && WaitForSingleObject(h, (uint)graceMs) == WAIT_OBJECT_0;
                        SetConsoleCtrlHandler(IntPtr.Zero, false);
                        if (exited) return true;
                    }
                }
                TerminateProcess(h, 1);
                WaitForSingleObject(h, 3000);
                return false;
            }
            finally { CloseHandle(h); }
        }

        public static bool IsAlive(IntPtr handle)
        {
            return handle != IntPtr.Zero && WaitForSingleObject(handle, 0) == WAIT_TIMEOUT;
        }

        // Runs a hidden process with deadlock-free async stdout/stderr capture and bounded kill timeout.
        public static int RunBounded(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = "";
            stderr = "";
            if (string.IsNullOrEmpty(exe)) return -1;
            int waitMs = Math.Max(300, timeoutMs);
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            var psi = new ProcessStartInfo(exe, args ?? "")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                if (p == null) return -1;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(waitMs))
                {
                    try { p.Kill(); } catch { }
                    p.WaitForExit(300);
                    lock (outSb) stdout = outSb.ToString();
                    lock (errSb) stderr = errSb.ToString();
                    return -1;
                }
                p.WaitForExit(); // flush async output event handlers
                lock (outSb) stdout = outSb.ToString();
                lock (errSb) stderr = errSb.ToString();
                return p.ExitCode;
            }
        }

        public static int ExitCode(IntPtr handle)
        {
            uint code;
            if (handle != IntPtr.Zero && GetExitCodeProcess(handle, out code))
                return unchecked((int)code);
            return -1;
        }

        public static void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        public static string ExePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
            }
            finally { CloseHandle(h); }
        }

        // Fast command line lookup via ProcessCommandLineInformation (60), avoiding WMI overhead
        public static string CommandLine(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint len = 0;
                NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out len);
                if (len == 0 || len > 65536) return null;
                buf = Marshal.AllocHGlobal((int)len);
                if (NtQueryInformationProcess(h, 60, buf, len, out len) != 0) return null;
                var us = (UNICODE_STRING)Marshal.PtrToStructure(buf, typeof(UNICODE_STRING));
                if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
                return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            catch { return null; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }

        public static HashSet<int> WindowedPids()
        {
            var pids = new HashSet<int>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != 0) pids.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
            return pids;
        }
    }
}
