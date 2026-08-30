using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace TaskbarLyrics;

/// <summary>
/// macOS-menubar-style module: a borderless, click-through strip showing the
/// name of the app that currently has focus. Unlike the lyrics and visualizer
/// modules there is no render loop at all — it repaints only when the
/// <see cref="ForegroundWatcher"/> reports a different app.
/// </summary>
public sealed class AppNameWindow : Window
{
    private readonly Config _cfg;
    private readonly AppNameCanvas _canvas;
    private readonly System.Windows.Threading.DispatcherTimer _maintain;
    private readonly ForegroundWatcher _watcher = new();

    public bool Enabled { get; private set; }
    public string Placement => _cfg.AppNamePlacement;
    public string VPos => StripLayout.NormVPos(_cfg.AppNameVPos);
    public string Align => StripLayout.NormAlign(_cfg.AppNameAlign);
    public int XOffset => _cfg.AppNameXOffset;
    public double FontPx => _cfg.AppNameFontPx;

    public AppNameWindow(Config cfg)
    {
        _cfg = cfg;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        Content = _canvas = new AppNameCanvas(cfg);

        SourceInitialized += (_, _) => ApplyClickThrough();
        Loaded += (_, _) => Reposition();

        _watcher.AppChanged += name =>
        {
            Log.Write($"appname: {name}");
            Dispatcher.BeginInvoke(() => _canvas.SetAppName(name));
        };

        _maintain = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _maintain.Tick += (_, _) => { Reposition(); ReassertTopmost(); };
    }

    // ---- enable / disable (tray) ----

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (on)
        {
            Show();
            Reposition();
            _watcher.Start();
            _maintain.Start();
        }
        else
        {
            _maintain.Stop();
            _watcher.Stop();
            _canvas.SetAppName(null);
            Hide();
        }
        _cfg.AppNameEnabled = on;
        _cfg.Save();
    }

    // ---- position / appearance (tray) ----

    public void SetVPos(string vpos)
    {
        _cfg.AppNameVPos = StripLayout.NormVPos(vpos);
        _cfg.Save();
        Reposition();
    }

    public void SetAlign(string align)
    {
        _cfg.AppNameAlign = StripLayout.NormAlign(align);
        _cfg.Save();
        Reposition();
        _canvas.InvalidateVisual();   // alignment also moves the text inside the strip
    }

    public void SetXOffset(int px)
    {
        _cfg.AppNameXOffset = Math.Clamp(px, 0, 4000);
        _cfg.Save();
        Reposition();
    }

    public void SetFontSize(int px)
    {
        _cfg.AppNameFontPx = Math.Clamp(px, 8, 48);
        _cfg.Save();
        _canvas.RebuildFont();
        Reposition();
    }

    /// <summary>Repaint after an appearance change made elsewhere (e.g. the shadow toggle).</summary>
    public void RefreshAppearance() => _canvas.InvalidateVisual();

    public void TogglePlacement()
    {
        _cfg.AppNamePlacement = _cfg.AppNamePlacement == "taskbar" ? "above" : "taskbar";
        _cfg.Save();
        Reposition();
    }

    private void Reposition() => StripLayout.Apply(this, StripLayout.Compute(
        VPos, _cfg.AppNamePlacement, Align, _cfg.AppNameXOffset,
        _cfg.AppNameWidth, Math.Max(22, _cfg.AppNameFontPx * 2.1)));

    // ---- win32: click-through + never-activate + stay above the taskbar ----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>Draws the app name once per focus change. No animation, no per-frame work.</summary>
public sealed class AppNameCanvas : FrameworkElement
{
    private readonly Config _cfg;
    private Typeface _typeface;
    private SolidColorBrush _fill, _shadow;
    private string? _name;
    private FormattedText? _text, _textShadow;

    public AppNameCanvas(Config cfg)
    {
        _cfg = cfg;
        IsHitTestVisible = false;
        _typeface = MakeTypeface(cfg);
        _fill = Frozen(Color.FromArgb(cfg.AppNameAlpha, 255, 255, 255));
        _shadow = Frozen(Color.FromArgb(150, 0, 0, 0));
    }

    private static Typeface MakeTypeface(Config cfg) =>
        new(new FontFamily($"{cfg.FontFamily}, Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Re-reads font settings after a tray change.</summary>
    public void RebuildFont()
    {
        _typeface = MakeTypeface(_cfg);
        _fill = Frozen(Color.FromArgb(_cfg.AppNameAlpha, 255, 255, 255));
        _text = _textShadow = null;
        InvalidateVisual();
    }

    public void SetAppName(string? name)
    {
        if (name == _name) return;
        _name = name;
        _text = _textShadow = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(_name) || ActualWidth < 20) return;

        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_text == null)
        {
            FormattedText Make(Brush b) =>
                new(_name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    _typeface, _cfg.AppNameFontPx, b, ppd)
                { MaxLineCount = 1, Trimming = System.Windows.TextTrimming.CharacterEllipsis,
                  MaxTextWidth = Math.Max(20, ActualWidth - 4) };
            _text = Make(_fill);
            _textShadow = Make(_shadow);
        }

        var x = StripLayout.AlignX(StripLayout.NormAlign(_cfg.AppNameAlign), _text.Width, ActualWidth, 2);
        var y = (ActualHeight - _text.Height) / 2;
        if (_cfg.TextShadow) dc.DrawText(_textShadow!, new Point(x, y + 1));
        dc.DrawText(_text, new Point(x, y));
    }
}
