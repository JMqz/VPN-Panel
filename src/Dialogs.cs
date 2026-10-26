// Dark dialogs that match the main window: confirm, info, text prompt and the running-apps picker.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

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

        static Button LinkButton(string text, string url, bool filled)
        {
            var b = new Button { Style = S("GhostButton"), Background = filled ? B("Hover") : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = text });
            label.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            b.Content = label;
            b.Click += (s, e) => { try { Process.Start(url); } catch { } };
            return b;
        }

        // Numbered step: badge + text (+ optional controls below)
        static UIElement GuideStep(string number, string text, params UIElement[] extra)
        {
            var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = B("Surface2"), VerticalAlignment = VerticalAlignment.Top };
            badge.Child = new TextBlock { Text = number, FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(badge);
            var sp = new StackPanel { Margin = new Thickness(12, 2, 0, 0) };
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            foreach (UIElement x in extra) sp.Children.Add(x);
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            return g;
        }

        // How to get a server (.conf) from the VPN provider. Proton first: it's what people use and it's free.
        // While it's open the main window watches Downloads and closes it when the file arrives.
        // ShowDialog() returns true when the user wants to pick the file by hand.
        public static Window ProviderGuide(Window owner, bool otherProvider)
        {
            Window d = Frame(owner, "Adicionar servidor", 370);
            var body = new StackPanel();

            var proton = new StackPanel();
            proton.Children.Add(Message("O Proton VPN gera um arquivo com o servidor. Leva uns dois minutos e funciona também no plano grátis."));
            var signup = LinkButton("Não tenho conta: criar grátis", "https://account.protonvpn.com/signup?plan=free", false);
            signup.Margin = new Thickness(-12, 2, 0, 0);
            signup.Foreground = B("Accent");
            var openProton = LinkButton("Abrir o Proton", "https://account.protonvpn.com/downloads", true);
            openProton.Margin = new Thickness(0, 8, 0, 0);
            proton.Children.Add(GuideStep("1", "Abra o site do Proton e entre na sua conta.", openProton, signup));
            proton.Children.Add(GuideStep("2", "Em Downloads, desça até “Configuração do WireGuard” (WireGuard configuration)."));
            proton.Children.Add(GuideStep("3", "Digite um nome (ex.: PC), escolha Windows e um servidor. Clique em Criar e depois em Baixar."));

            var other = new StackPanel { Visibility = Visibility.Collapsed };
            other.Children.Add(Message("Cada provedor gera o arquivo no site, na área de configuração manual do WireGuard. Procure por “WireGuard” ou “configuração manual”."));
            var mullvad = new Border { Style = S("Card"), Margin = new Thickness(0, 12, 0, 0), Background = B("Surface2") };
            var mv = new StackPanel();
            mv.Children.Add(new TextBlock { Text = "Mullvad", FontWeight = FontWeights.SemiBold });
            mv.Children.Add(new TextBlock { Text = "Entre na conta → Gerador de configuração WireGuard → Windows. Escolha o país e baixe (pode ser um .zip com vários).", Style = S("Caption"), Margin = new Thickness(0, 4, 0, 8), LineHeight = 18 });
            mv.Children.Add(LinkButton("Abrir site", "https://mullvad.net/account/wireguard-config", true));
            mullvad.Child = mv;
            other.Children.Add(mullvad);

            var swap = new Button { Style = S("GhostButton"), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(-12, 10, 0, 0), Content = "Usa outro provedor?", Foreground = B("Accent") };
            swap.Click += (s, e) =>
            {
                bool toOther = other.Visibility != Visibility.Visible;
                other.Visibility = toOther ? Visibility.Visible : Visibility.Collapsed;
                proton.Visibility = toOther ? Visibility.Collapsed : Visibility.Visible;
                swap.Content = toOther ? "Voltar para o Proton VPN" : "Usa outro provedor?";
            };
            if (otherProvider) swap.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // waiting for the download
            var wait = new Border { Style = S("Card"), Margin = new Thickness(0, 16, 0, 0), Background = B("AccentSoft"), Padding = new Thickness(12, 10, 12, 10) };
            var wg = new Grid();
            wg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            wg.ColumnDefinitions.Add(new ColumnDefinition());
            var spin = new Ellipse { Width = 16, Height = 16, Stroke = B("Accent"), StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 8, 200 }, StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            var rotate = new RotateTransform();
            spin.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            wg.Children.Add(spin);
            var wt = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            wt.Children.Add(new TextBlock { Text = "Esperando o arquivo...", FontWeight = FontWeights.SemiBold });
            wt.Children.Add(new TextBlock { Text = "Quando ele chegar na pasta Downloads, entra aqui sozinho.", Style = S("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(wt, 1);
            wg.Children.Add(wt);
            wait.Child = wg;

            body.Children.Add(proton);
            body.Children.Add(other);
            body.Children.Add(swap);
            body.Children.Add(wait);

            Button pick = Btn("Já baixei", "GhostButton");
            pick.Click += (s, e) => d.DialogResult = true;
            Button close = Btn("Fechar", "GhostButton");
            close.IsCancel = true;
            close.Click += (s, e) => d.Close();
            Layout(d, "Adicionar servidor", body, pick, close);
            return d;
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
