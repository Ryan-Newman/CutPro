using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace CutPro;

public enum AlertKind { Info, Warning, Error, Question, Progress }

/// <summary>
/// Themed "glass" message boxes replacing MessageBox: Windows 11 acrylic backdrop (DWM system backdrop) tinted with the
/// editor theme, light edge highlight and sheen, the owner window dimmed behind it. Older Windows falls back to an
/// opaque themed panel. buttons[0] is the default (Enter); the last button is Esc when there are several.
/// </summary>
public static class Alert
{
    /// <summary>Test hook: when set, alerts don't open — this returns the chosen button index (and can log the text).</summary>
    public static Func<string, string, IReadOnlyList<string>, int>? Auto;
    /// <summary>Test hook (screenshots): runs once a modal dialog is on screen — capture it, then close it.</summary>
    public static Func<Window, Task>? Shown;
    internal static void Hook(Window w)
    {
        if (Shown is not { } f) return;
        bool done = false;
        w.ContentRendered += async (_, _) => { if (done) return; done = true; await f(w); };
    }

    public static int Show(Window? owner, string message, string title, AlertKind kind = AlertKind.Info, params string[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { "OK" };
        if (Auto != null) return Auto(title, message, buttons);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var w = new AlertWindow(title, message, kind, buttons);
        if (owner is { IsLoaded: true, IsVisible: true } && !ReferenceEquals(owner, w)) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Hook(w);
        using (Scrim.Over(w.Owner)) w.ShowDialog();
        return w.Result;
    }

    /// <summary>A glass dialog with a form (extra) under the message; returns the button index.</summary>
    public static int Show(Window? owner, string message, string title, AlertKind kind, string[] buttons, UIElement extra)
    {
        if (Auto != null) return Auto(title, message, buttons);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var w = new AlertWindow(title, message, kind, buttons, extra);
        if (owner is { IsLoaded: true, IsVisible: true }) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Hook(w);
        using (Scrim.Over(w.Owner)) w.ShowDialog();
        return w.Result;
    }

    public static bool Confirm(Window? owner, string message, string title, string yes = "OK", string no = "Cancel", AlertKind kind = AlertKind.Question)
        => Show(owner, message, title, kind, yes, no) == 0;

    /// <summary>Non-modal alert (screenshots / tests). Close the returned window yourself.</summary>
    public static Window Preview(Window owner, string message, string title, AlertKind kind, params string[] buttons)
    {
        var w = new AlertWindow(title, message, kind, buttons.Length == 0 ? new[] { "OK" } : buttons) { Owner = owner };
        var dim = Scrim.Over(owner);
        w.Closed += (_, _) => dim.Dispose();
        w.Show();
        return w;
    }

    /// <summary>Glass progress dialog (export): text + bar + Cancel. Closing it (Esc, Alt+F4, Cancel) calls onCancel and
    /// keeps it open until <see cref="ProgressDialog.Close"/>.</summary>
    public sealed class ProgressDialog
    {
        readonly AlertWindow w; readonly ProgressBar bar; readonly IDisposable dim;
        public ProgressDialog(Window owner, string title, string text, Action onCancel)
        {
            bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 2), MinWidth = 320 };
            w = new AlertWindow(title, text, AlertKind.Progress, new[] { "Cancel" }, bar, _ => { onCancel(); Text = "Cancelling…"; return false; })
            { Owner = owner, AllowClose = false };
            dim = Scrim.Over(owner);
            w.Show();
        }
        public string Text { set => w.Message.Text = value; }
        public double Value { set => bar.Value = value; }
        public void Close() { w.AllowClose = true; w.Close(); dim.Dispose(); }
    }

    /// <summary>Dims the owner's content while a dialog is open (fades in/out).</summary>
    sealed class Scrim : IDisposable
    {
        Border? b; Panel? host;
        public static Scrim Over(Window? owner)
        {
            var s = new Scrim();
            if (owner?.Content is not Panel p) return s;
            s.host = p;
            s.b = new Border { IsHitTestVisible = false, Opacity = 0 };
            s.b.SetResourceReference(Border.BackgroundProperty, "B.Scrim");
            Panel.SetZIndex(s.b, 10000);
            if (p is Grid) { Grid.SetRowSpan(s.b, 1000); Grid.SetColumnSpan(s.b, 1000); }
            p.Children.Add(s.b);
            s.b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            return s;
        }
        public void Dispose()
        {
            if (b == null || host == null) return;
            var (bb, hh) = (b, host); b = null;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => hh.Children.Remove(bb);
            bb.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}

public sealed class AlertWindow : Window
{
    public int Result;
    public TextBox Message = null!;
    /// <summary>Set to allow closing when a button handler keeps the window open (progress dialogs).</summary>
    public bool AllowClose = true;
    readonly Border root;
    readonly int cancelIndex;

    public AlertWindow(string title, string message, AlertKind kind, string[] buttons, UIElement? extra = null, Func<int, bool>? onButton = null)
    {
        Title = title;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 400; MaxWidth = 580;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        UseLayoutRounding = true;
        // keep a DWM frame (needed for the backdrop) but draw no caption: the whole client area is ours
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0),
                                                             CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
        cancelIndex = buttons.Length - 1;
        Result = cancelIndex;

        // ---- content ----
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // header: icon + title
        var (glyph, colorKey) = kind switch
        {
            AlertKind.Warning => ("", "Warn"),
            AlertKind.Error => ("", "Error"),
            AlertKind.Progress => ("", "Accent"),
            AlertKind.Question => ("", "Accent"),
            _ => ("", "Accent"),
        };
        var body = new Grid { Margin = new Thickness(22, 20, 24, 20) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 26, Margin = new Thickness(0, 1, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "B." + colorKey);
        icon.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55, Color = ThemeManager.Color(colorKey) };
        body.Children.Add(icon);
        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var head = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
        var msg = new TextBox
        {
            Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false,
            FontSize = 13, Cursor = Cursors.IBeam,
        };
        msg.Style = new Style(typeof(TextBox));        // no themed input chrome: plain selectable text
        msg.SetResourceReference(TextBox.ForegroundProperty, "B.Fg");
        msg.SetResourceReference(TextBox.SelectionBrushProperty, "B.Accent");
        text.Children.Add(head); text.Children.Add(msg); Message = msg;
        if (extra != null) text.Children.Add(extra);
        body.Children.Add(text);
        grid.Children.Add(body);
        // footer: buttons (primary first on the right, like Windows 11 dialogs: [Primary] [Secondary] …)
        var foot = new Border { Padding = new Thickness(20, 14, 20, 16), CornerRadius = new CornerRadius(0, 0, 8, 8) };
        foot.SetResourceReference(Border.BackgroundProperty, "B.Glass.Footer");
        Grid.SetRow(foot, 1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int k = i;
            var b = new Button { Content = buttons[i], MinWidth = 96, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), IsDefault = i == 0,
                                 IsCancel = i == cancelIndex };
            if (i == 0) b.SetResourceReference(StyleProperty, "Primary");
            b.Click += (_, _) => { if (onButton?.Invoke(k) == false) return; Result = k; Close(); };
            row.Children.Add(b);
        }
        foot.Child = row;
        grid.Children.Add(foot);

        // glass layers: theme tint over the acrylic, sheen, light edge
        root = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        root.SetResourceReference(Border.BackgroundProperty, "B.Glass.Tint");
        root.SetResourceReference(Border.BorderBrushProperty, "B.Glass.Edge");
        var sheen = new Border { CornerRadius = new CornerRadius(8), IsHitTestVisible = false, Height = 90, VerticalAlignment = VerticalAlignment.Top };
        sheen.SetResourceReference(Border.BackgroundProperty, "B.Glass.Sheen");
        var layers = new Grid();
        layers.Children.Add(sheen); layers.Children.Add(grid);
        root.Child = layers;
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = new ScaleTransform(0.96, 0.96);
        root.Opacity = 0;
        Content = root;

        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox && e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        KeyDown += (_, e) => { if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && msg.SelectionLength == 0) Clipboard.SetText(title + "\n\n" + message); };
        // explicit size from the content (SizeToContent + WindowChrome adds the absent caption's height as an empty strip)
        root.Measure(new Size(MaxWidth, double.PositiveInfinity));
        Width = Math.Clamp(Math.Ceiling(root.DesiredSize.Width), MinWidth, MaxWidth);
        root.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Ceiling(root.DesiredSize.Height);
        SourceInitialized += (_, _) => Backdrop(this, root);
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; onButton?.Invoke(cancelIndex); } };
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            var s = new DoubleAnimation(1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
            ((ScaleTransform)root.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, s);
            ((ScaleTransform)root.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, s);
            ((Button)row.Children[0]).Focus();
        };
    }

    // ---- Windows 11 acrylic ----
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_TRANSIENTWINDOW = 3;

    /// <summary>True when the acrylic backdrop is available (Windows 11 22H2+).</summary>
    public static bool GlassSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Windows 11 acrylic behind a borderless window (any glass window: alerts, the publish window).</summary>
    internal static void Backdrop(Window w, Border root)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetWindowLong(hwnd, -16, GetWindowLong(hwnd, -16) & ~0x80000);     // no WS_SYSMENU: DWM draws no caption buttons over the glass
        int dark = ThemeManager.Current.Dark ? 1 : 0, round = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        if (!GlassSupported)
        {
            root.SetResourceReference(Border.BackgroundProperty, "B.Popup.Bg");      // no acrylic: opaque themed panel
            return;
        }
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        int type = DWMSBT_TRANSIENTWINDOW;
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4) != 0)
            root.SetResourceReference(Border.BackgroundProperty, "B.Popup.Bg");
    }
}

/// <summary>Screen capture of a region (includes DWM effects such as acrylic, unlike RenderTargetBitmap). For --uishot.</summary>
static class ScreenGrab
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    /// <summary>Captures the window's on-screen rectangle (device pixels).</summary>
    public static BitmapSource Window(Window w)
    {
        var tl = w.PointToScreen(new Point(0, 0));
        var br = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight));
        int x = (int)tl.X, y = (int)tl.Y, cw = (int)(br.X - tl.X), ch = (int)(br.Y - tl.Y);
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, cw, ch);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, cw, ch, screen, x, y, 0x00CC0020 | 0x40000000);   // SRCCOPY | CAPTUREBLT
        SelectObject(mem, old);
        var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return src;
    }
}
