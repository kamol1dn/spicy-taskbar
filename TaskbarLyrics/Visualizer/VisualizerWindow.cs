using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarLyrics;

/// <summary>
/// Full-width, click-through visualizer surface pinned to the BOTTOM of the
/// z-order and sized to the lower third of the primary screen. Because it sits
/// below every normal app window, it only shows over the desktop and through the
/// (transparent) taskbar — it never bleeds over an active window. It also owns
/// the audio engine, the current preset, and the album palette, and can be fully
/// torn down (device released, render loop detached) when disabled from the tray.
/// </summary>
public sealed class VisualizerWindow : Window
{
    private readonly Config _cfg;
    private readonly AudioEngine _audio = new();
    private readonly VizCanvas _canvas;
    private readonly DispatcherTimer _maintain;
    private EventHandler? _renderHandler;
    private long _lastFrameTick;
    private double _clock;
    private double _lastDtMs = 16;
    private bool _idle;

    // Cap the visualizer to ~45fps regardless of the monitor refresh rate — plenty
    // smooth for an ambient background, and keeps CPU down on high-refresh displays.
    private const double FrameIntervalMs = 1000.0 / 45;
    private long _lastPaintTick;

    public IVisualizerPreset Preset { get; private set; }
    public AlbumPalette Palette { get; private set; } = AlbumPalette.Default;
    public bool Enabled { get; private set; }
    public bool Randomize { get; private set; }

    /// <summary>Global dimming applied to every preset so it reads as a backdrop.</summary>
    internal double MasterOpacity => Math.Clamp(_cfg.VizOpacity, 0.05, 1.0);

    public VisualizerWindow(Config cfg)
    {
        _cfg = cfg;
        Preset = VisualizerPresets.Get(cfg.VizPreset);
        Randomize = cfg.VizRandomizeOnTrack;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = false;                    // deliberately NOT topmost — we live at the back
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        Content = _canvas = new VizCanvas(this);

        SourceInitialized += (_, _) => { ApplyExStyles(); SendToBottom(); };
        Loaded += (_, _) => { Reposition(); SendToBottom(); };
        Closed += (_, _) => _audio.Dispose();

        _maintain = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _maintain.Tick += (_, _) => { if (Enabled) { Reposition(); SendToBottom(); } };
    }

    // ---- enable / disable (tray) ----

    public void SetEnabled(bool on)
    {
        if (on == Enabled && (on ? IsVisible : true)) { PersistEnabled(on); return; }
        Enabled = on;
        if (on)
        {
            Show();
            Reposition();
            SendToBottom();
            _audio.Start();
            _idle = false;
            _maintain.Start();
            if (_renderHandler == null)
            {
                _renderHandler = (_, _) => OnFrame();
                CompositionTarget.Rendering += _renderHandler;
            }
        }
        else
        {
            if (_renderHandler != null)
            {
                CompositionTarget.Rendering -= _renderHandler;
                _renderHandler = null;
            }
            _maintain.Stop();
            _audio.Stop();
            Hide();
        }
        PersistEnabled(on);
    }

    private void PersistEnabled(bool on) { _cfg.VizEnabled = on; _cfg.Save(); }

    public void SetPreset(string id, bool persist = true)
    {
        Preset = VisualizerPresets.Get(id);
        if (persist) { _cfg.VizPreset = Preset.Id; _cfg.Save(); }
    }

    public void SetRandomize(bool on)
    {
        Randomize = on;
        _cfg.VizRandomizeOnTrack = on;
        _cfg.Save();
    }

    /// <summary>Called on every real track change; rerolls the preset if randomize is on.</summary>
    public void OnTrackChanged()
    {
        if (Randomize && Enabled)
            SetPreset(VisualizerPresets.Random(Preset.Id).Id, persist: false);
    }

    /// <summary>Recompute the gradient palette from new album-art bytes (off the UI thread).</summary>
    public void SetArtwork(byte[]? bytes)
    {
        _ = Task.Run(() =>
        {
            var pal = AlbumPalette.FromImageBytes(bytes);
            Dispatcher.BeginInvoke(() => Palette = pal);
        });
    }

    // ---- per-frame ----

    private void OnFrame()
    {
        // Throttle to the target frame rate — CompositionTarget.Rendering fires at
        // the display's refresh rate, which can be 120/144Hz+.
        var tick = Stopwatch.GetTimestamp();
        if (_lastPaintTick != 0 &&
            (tick - _lastPaintTick) / (double)Stopwatch.Frequency * 1000 < FrameIntervalMs)
            return;
        _lastPaintTick = tick;

        double dt = _lastFrameTick == 0 ? 16
            : Math.Min(100, (tick - _lastFrameTick) / (double)Stopwatch.Frequency * 1000);
        _lastFrameTick = tick;
        _lastDtMs = dt;
        _clock += dt / 1000.0;

        // Skip repaints while the output is silent and has fully decayed, so paused
        // music costs nothing — but keep advancing the analyzer so we notice audio
        // returning and draw one last frame to clear the bars on the way down.
        bool live = _audio.IsActive || _audio.Level > 0.004 || _audio.Bass > 0.004;
        if (live)
        {
            _audio.Update(dt);
            _canvas.InvalidateVisual();
            _idle = false;
        }
        else if (!_idle)
        {
            _audio.Update(dt);
            _canvas.InvalidateVisual();
            _idle = true;
        }
    }

    internal VizFrame BuildFrame()
    {
        double screenH = SystemParameters.PrimaryScreenHeight;
        double taskbarH = Math.Max(0, screenH - SystemParameters.WorkArea.Bottom);
        if (taskbarH < 6) taskbarH = Math.Min(48, ActualHeight);
        return new VizFrame
        {
            Width = ActualWidth,
            Height = ActualHeight,
            TaskbarHeight = Math.Min(taskbarH, ActualHeight),
            Spectrum = _audio.Spectrum,
            Level = _audio.Level,
            Bass = _audio.Bass,
            Palette = Palette,
            TimeSec = _clock,
            DtMs = _lastDtMs,
        };
    }

    private void Reposition()
    {
        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        double h = Math.Max(80, screenH * Math.Clamp(_cfg.VizHeightFraction, 0.15, 0.6));
        if (Math.Abs(Left) > 0.5 || Math.Abs(Top - (screenH - h)) > 0.5 ||
            Math.Abs(Width - screenW) > 0.5 || Math.Abs(Height - h) > 0.5)
        {
            Left = 0; Top = screenH - h; Width = screenW; Height = h;
        }
    }

    // ---- win32: click-through, never-activate, sit at the bottom ----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void ApplyExStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private void SendToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

/// <summary>Immediate-mode surface: each frame hands the active preset a fresh <see cref="VizFrame"/>.</summary>
public sealed class VizCanvas : FrameworkElement
{
    private readonly VisualizerWindow _w;

    public VizCanvas(VisualizerWindow w)
    {
        _w = w;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;
        dc.PushOpacity(_w.MasterOpacity);
        _w.Preset.Render(dc, _w.BuildFrame());
        dc.Pop();
    }
}
