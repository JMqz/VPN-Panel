// VPN Panel - Chrome: DWM window borders and dark title bar.

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
    // Borderless dark window with native shadow and rounded corners (Windows 11)
    static class Chrome
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window w, double captionHeight)
        {
            WindowChrome.SetWindowChrome(w, new WindowChrome
            {
                CaptionHeight = captionHeight,
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            w.SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int dark = 1, round = 2, border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE (no window outline)
                DwmSetWindowAttribute(h, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
                DwmSetWindowAttribute(h, 34, ref border, 4);  // DWMWA_BORDER_COLOR
            };
        }
    }
}
