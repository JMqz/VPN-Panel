// Dark dialogs that match the main window: confirm, info, text prompt and the running-apps picker.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VpnPanel
{
    static class Dialogs
    {
        public static Brush B(string key) { return (Brush)Application.Current.FindResource(key); }
        public static Style S(string key) { return (Style)Application.Current.FindResource(key); }

        public static Window Frame(Window owner, string title, double width)
        {
            var d = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = B("Surface"),
                Foreground = B("Text"),
                FontFamily = (FontFamily)Application.Current.FindResource("Font"),
                FontSize = 13,
                UseLayoutRounding = true,
            };
            // a hidden main window (in the tray) can't own a dialog
            if (owner != null && owner.IsVisible)
            {
                d.Owner = owner;
                d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Chrome.Apply(d, 0);
            return d;
        }

        public static StackPanel Layout(Window d, string title, UIElement body, params Button[] buttons)
        {
            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            heading.MouseLeftButtonDown += (s, e) => d.DragMove();
            root.Children.Add(heading);
            if (body != null) root.Children.Add(body);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            foreach (Button b in buttons)
            {
                b.Margin = new Thickness(8, 0, 0, 0);
                b.MinWidth = 88;
                bar.Children.Add(b);
            }
            root.Children.Add(bar);
            d.Content = root;
            return root;
        }

        public static Button Btn(string text, string style)
        {
            return new Button { Content = text, Style = S(style) };
        }

        public static TextBlock Message(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = B("Muted"), LineHeight = 19 };
        }

        public static bool Confirm(Window owner, string title, string message, string okText, bool danger)
        {
            Window d = Frame(owner, title, 360);
            Button ok = Btn(okText, danger ? "DangerButton" : "PrimaryButton");
            Button cancel = Btn("Cancelar", "GhostButton");
            ok.IsDefault = true;
            cancel.IsCancel = true;
            ok.Click += (s, e) => d.DialogResult = true;
            Layout(d, title, Message(message), cancel, ok);
            return d.ShowDialog() == true;
        }

        public static void Info(Window owner, string title, string message)
        {
            Window d = Frame(owner, title, 360);
            Button ok = Btn("OK", "PrimaryButton");
            ok.IsDefault = true;
            ok.IsCancel = true;
            Layout(d, title, Message(message), ok);
            d.ShowDialog();
        }

        // Where to get WireGuard .conf files, per provider
        public static void ConfHelp(Window owner)
        {
            Window d = Frame(owner, "Como conseguir o arquivo .conf", 380);
            var body = new StackPanel();
            body.Children.Add(Message("O arquivo .conf é a \"chave\" do servidor. Cada provedor de VPN gera o seu no site, na área de configuração manual do WireGuard."));
            Action<string, string, string> provider = (name, steps, url) =>
            {
                var card = new Border { Style = S("Card"), Margin = new Thickness(0, 12, 0, 0), Background = B("Surface2") };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
                sp.Children.Add(new TextBlock { Text = steps, Style = S("Caption"), Margin = new Thickness(0, 4, 0, 8), LineHeight = 18 });
                var open = new Button { Style = S("GhostButton"), Background = B("Hover"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = "Abrir site" });
                label.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                open.Content = label;
                open.Click += (s, e) => { try { Process.Start(url); } catch { } };
                sp.Children.Add(open);
                card.Child = sp;
                body.Children.Add(card);
            };
            provider("Proton VPN", "Entre na sua conta → Downloads → Configurações do WireGuard. Escolha Windows, um servidor e clique em Criar.", "https://account.protonvpn.com/downloads");
            provider("Mullvad", "Entre na sua conta → Gerador de configuração WireGuard → Windows. Escolha o país e baixe.", "https://mullvad.net/account/wireguard-config");
            body.Children.Add(new TextBlock
            {
                Text = "Outros provedores: procure por \"WireGuard\" ou \"configuração manual\" no site. Depois é só adicionar o arquivo aqui no app.",
                Style = S("Caption"), Margin = new Thickness(0, 12, 0, 0), LineHeight = 18,
            });
            Button ok = Btn("Entendi", "PrimaryButton");
            ok.IsDefault = true;
            ok.IsCancel = true;
            Layout(d, "Como conseguir o arquivo .conf", body, ok);
            d.ShowDialog();
        }

        // Returns the text, or null when cancelled. validate returns an error message or null.
        public static string Prompt(Window owner, string title, string message, string initial, Func<string, string> validate)
        {
            Window d = Frame(owner, title, 360);
            var body = new StackPanel();
            if (message != null) body.Children.Add(Message(message));
            var box = new TextBox { Style = S("Input"), Text = initial, Margin = new Thickness(0, message != null ? 12 : 0, 0, 0) };
            var error = new TextBlock { Foreground = B("Danger"), FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            body.Children.Add(box);
            body.Children.Add(error);
            Button ok = Btn("Salvar", "PrimaryButton");
            Button cancel = Btn("Cancelar", "GhostButton");
            ok.IsDefault = true;
            cancel.IsCancel = true;
            ok.Click += (s, e) =>
            {
                string msg = validate(box.Text.Trim());
                if (msg == null) { d.DialogResult = true; return; }
                error.Text = msg;
                error.Visibility = Visibility.Visible;
            };
            Layout(d, title, body, cancel, ok);
            d.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return d.ShowDialog() == true ? box.Text.Trim() : null;
        }

        static readonly HashSet<string> HiddenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationFrameHost", "cmd", "conhost", "csrss", "ctfmon", "dllhost", "dwm", "fontdrvhost", "Idle",
            "LockApp", "RuntimeBroker", "SearchHost", "SecurityHealthSystray", "ShellExperienceHost", "sihost",
            "StartMenuExperienceHost", "svchost", "System", "taskhostw", "TextInputHost", "VpnPanel",
            "WidgetService", "Widgets", "winlogon", "wiresock-client",
        };

        class ProcessEntry { public string Name, Path; public bool Windowed; }

        // Lets the user tick apps that are running now; returns their process names
        public static List<string> PickProcesses(Window owner, Func<string, string, ImageSource> icon)
        {
            int session = Process.GetCurrentProcess().SessionId;
            var entries = new Dictionary<string, ProcessEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != session || HiddenProcesses.Contains(p.ProcessName)) continue;
                    ProcessEntry e;
                    if (!entries.TryGetValue(p.ProcessName, out e))
                    {
                        e = new ProcessEntry { Name = p.ProcessName };
                        try { e.Path = p.MainModule.FileName; } catch { }
                        entries[p.ProcessName] = e;
                    }
                    if (p.MainWindowHandle != IntPtr.Zero) e.Windowed = true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            List<ProcessEntry> list = entries.Values.OrderBy(e => !e.Windowed).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

            Window d = Frame(owner, "Apps abertos", 340);
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var body = new StackPanel();
            var filter = new TextBox { Style = S("Input"), Tag = "Filtrar" };
            var rows = new StackPanel();
            var scroll = new ScrollViewer { Height = 330, Margin = new Thickness(0, 10, -8, 0), Padding = new Thickness(0, 0, 8, 0), Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            body.Children.Add(filter);
            body.Children.Add(scroll);
            Button ok = Btn("Adicionar", "PrimaryButton");
            Button cancel = Btn("Cancelar", "GhostButton");
            cancel.IsCancel = true;
            ok.IsEnabled = false;
            ok.Click += (s, e) => d.DialogResult = true;

            Action fill = null;
            fill = () =>
            {
                rows.Children.Clear();
                string f = filter.Text.Trim();
                bool? lastWindowed = null;
                foreach (ProcessEntry entry in list)
                {
                    if (f.Length > 0 && entry.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (lastWindowed != entry.Windowed)
                    {
                        rows.Children.Add(new TextBlock
                        {
                            Text = entry.Windowed ? "COM JANELA ABERTA" : "EM SEGUNDO PLANO",
                            FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"),
                            Margin = new Thickness(6, lastWindowed == null ? 2 : 12, 0, 6),
                        });
                        lastWindowed = entry.Windowed;
                    }
                    rows.Children.Add(PickRow(entry, icon, selected, () =>
                    {
                        ok.IsEnabled = selected.Count > 0;
                        ok.Content = selected.Count > 1 ? "Adicionar (" + selected.Count + ")" : "Adicionar";
                    }));
                }
            };
            filter.TextChanged += (s, e) => fill();
            fill();
            Layout(d, "Apps abertos", body, cancel, ok);
            d.Loaded += (s, e) => filter.Focus();
            return d.ShowDialog() == true ? selected.ToList() : new List<string>();
        }

        static UIElement PickRow(ProcessEntry entry, Func<string, string, ImageSource> icon, HashSet<string> selected, Action changed)
        {
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            UIElement img = AppAvatar(entry.Name, icon(entry.Name, entry.Path));
            g.Children.Add(img);
            var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            var check = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), Foreground = B("Accent"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
            Grid.SetColumn(check, 2);
            g.Children.Add(check);
            row.Child = g;

            Action paint = () =>
            {
                bool on = selected.Contains(entry.Name);
                check.Visibility = on ? Visibility.Visible : Visibility.Hidden;
                row.Background = on ? B("AccentSoft") : row.IsMouseOver ? B("Hover") : Brushes.Transparent;
            };
            if (selected.Contains(entry.Name)) paint();
            row.MouseEnter += (s, e) => paint();
            row.MouseLeave += (s, e) => paint();
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (!selected.Remove(entry.Name)) selected.Add(entry.Name);
                paint();
                changed();
            };
            return row;
        }

        // App icon, or a letter badge when there's no icon
        public static UIElement AppAvatar(string name, ImageSource icon)
        {
            if (icon != null) return new Image { Source = icon, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = B("Hover"), VerticalAlignment = VerticalAlignment.Center };
            b.Child = new TextBlock
            {
                Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return b;
        }
    }
}
