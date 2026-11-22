// Main window: dark renderer for the tray menu.

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
