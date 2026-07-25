using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace TaskbarLyrics;

/// <summary>
/// Borderless, click-through, always-on-top strip rendered over (or just above)
/// the taskbar. Draws the active lyric line with a per-syllable karaoke sweep,
/// background/filler vocals as a smaller second row, and interlude dots.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly Config _cfg;
    private readonly LyricsCanvas _canvas;
    private readonly System.Windows.Threading.DispatcherTimer _maintain;

    public OverlayWindow(Config cfg)
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

        Content = _canvas = new LyricsCanvas(cfg);

        SourceInitialized += (_, _) => ApplyClickThrough();
        Loaded += (_, _) => Reposition();

        // Redraw only when the output would actually differ. Paused playback,
        // held lines and instrumental gaps produce identical frames, so this
        // drops a continuous full-refresh-rate repaint to near zero.
        CompositionTarget.Rendering += (_, _) =>
        {
            if (_canvas.Tick()) _canvas.InvalidateVisual();
        };

        _maintain = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _maintain.Tick += (_, _) => { Reposition(); ReassertTopmost(); };
        _maintain.Start();
    }

    public void SetLyrics(Lyrics? lyrics) => _canvas.SetLyrics(lyrics);

    public void TogglePlacement()
    {
        _cfg.Placement = _cfg.Placement == "taskbar" ? "above" : "taskbar";
        Reposition();
    }

    private void Reposition()
    {
        var wa = SystemParameters.WorkArea;                 // DIPs, excludes taskbar
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        var taskbarH = screenH - wa.Bottom;                 // bottom taskbar assumed

        double h, top;
        if (_cfg.Placement == "taskbar" && taskbarH >= 24)
        {
            h = Math.Min(taskbarH - 2, 48);
            top = wa.Bottom + (taskbarH - h) / 2;
        }
        else
        {
            h = 48;
            top = wa.Bottom - h - 6;
        }

        var w = Math.Min(_cfg.Width, screenW * 0.62);
        double left = _cfg.Align switch
        {
            "center" => wa.Left + (screenW - w) / 2,
            "right" => wa.Left + screenW - w - _cfg.XOffset,
            _ => wa.Left + _cfg.XOffset,
        };

        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5 ||
            Math.Abs(Width - w) > 0.5 || Math.Abs(Height - h) > 0.5)
        {
            Left = left; Top = top; Width = w; Height = h;
        }
    }

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
}

/// <summary>Immediate-mode lyric renderer. All layout is cached per line and rebuilt only on line change.</summary>
public sealed class LyricsCanvas : FrameworkElement
{
    private readonly Config _cfg;
    private readonly Typeface _typeface;

    private Lyrics? _lyrics;
    private int _layoutLineIdx = -2;
    private LineLayout? _mainLayout;
    private BgVocal? _layoutBg;
    private LineLayout? _bgLayout;
    private long _lineShownTick;
    private double _bgAnim;      // animated 0..1 visibility of the filler row
    private long _lastFrameTick;

    private readonly SolidColorBrush _sung, _unsung, _bgBright, _bgDim, _shadow;

    public bool HasContent => _lyrics != null && PositionEngine.HasPosition;

    /// <summary>What the next frame would show. Pure arithmetic — no layout or drawing.</summary>
    private readonly record struct FrameState(
        bool Visible, int Idx, bool ShowDots, double GapStart, double GapEnd, BgVocal? Bg, double T);

    private FrameState ComputeState()
    {
        var ly = _lyrics;
        var now = PositionEngine.NowMs;
        if (ly == null || now == null) return default;
        var t = now.Value + _cfg.GlobalOffsetMs;

        var lines = ly.Lines;
        int idx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Start <= t) idx = i;
            else break;
        }

        bool showDots = false;
        double gapStart = 0, gapEnd = 0;
        if (idx == -1)
        {
            if (t < -500) return default;
            gapEnd = lines[0].Start;
            if (gapEnd >= _cfg.InterludeGapMs) showDots = true;
            else return default;
        }
        else if (t > lines[idx].End)
        {
            if (idx + 1 < lines.Count)
            {
                gapStart = lines[idx].End;
                gapEnd = lines[idx + 1].Start;
                if (gapEnd - gapStart >= _cfg.InterludeGapMs) showDots = true;
            }
            else if (t > lines[idx].End + 4000)
            {
                return default;
            }
        }

        BgVocal? activeBg = null;
        foreach (var bg in ly.Bg)
        {
            if (t >= bg.Start - 400 && t <= bg.End + 400) { activeBg = bg; break; }
            if (bg.Start - 400 > t) break;
        }

        return new FrameState(true, idx, showDots, gapStart, gapEnd, activeBg, t);
    }

    /// <summary>Quantized sweep position, in the time domain so no layout is needed.</summary>
    private static int SweepSig(List<Seg>? segs, double lineStart, double t)
    {
        if (segs == null) return t >= lineStart ? 1 : 0;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (t < s.Start) return i * 128;
            if (t <= s.End)
                return i * 128 + (int)((t - s.Start) / Math.Max(1, s.End - s.Start) * 127);
        }
        return segs.Count * 128;
    }

    /// <summary>
    /// Advances animation state and reports whether the visible output changed.
    /// Called once per display frame; returning false skips the repaint entirely.
    /// </summary>
    public bool Tick()
    {
        var st = ComputeState();

        // Animation clocks must keep running even while we skip repaints.
        var frameTick = Stopwatch.GetTimestamp();
        var dtMs = _lastFrameTick == 0 ? 16.0
            : Math.Min(100, (frameTick - _lastFrameTick) / (double)Stopwatch.Frequency * 1000);
        _lastFrameTick = frameTick;

        var bgTarget = st is { Visible: true, Bg: not null } ? 1.0 : 0.0;
        var step = dtMs / 220.0;
        _bgAnim = bgTarget > _bgAnim ? Math.Min(bgTarget, _bgAnim + step)
                                     : Math.Max(bgTarget, _bgAnim - step);

        int sig;
        if (!st.Visible)
        {
            sig = 0;
        }
        else
        {
            var ly = _lyrics!;
            var fadeMs = (frameTick - _lineShownTick) / (double)Stopwatch.Frequency * 1000;
            var fadeQ = (int)(Math.Clamp(fadeMs / 160.0, 0, 1) * 32);

            var mainSweep = st.ShowDots || st.Idx < 0 ? 0
                : SweepSig(ly.Lines[st.Idx].Sylls, ly.Lines[st.Idx].Start, st.T);
            var dotsQ = st.ShowDots
                ? (int)(Math.Clamp((st.T - st.GapStart) / Math.Max(1, st.GapEnd - st.GapStart), 0, 1) * 256)
                : 0;
            var bgSweep = st.Bg is { } b ? SweepSig(b.Sylls, b.Start, st.T) : 0;

            sig = HashCode.Combine(st.Idx, st.ShowDots, mainSweep, dotsQ,
                                   st.Bg?.Start ?? -1, bgSweep, (int)(_bgAnim * 256), fadeQ);
        }

        if (sig == _lastSig) return false;
        _lastSig = sig;
        return true;
    }

    private int _lastSig = int.MinValue;

    public LyricsCanvas(Config cfg)
    {
        _cfg = cfg;
        IsHitTestVisible = false;
        _typeface = new Typeface(new FontFamily($"{cfg.FontFamily}, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        _sung = Frozen(Color.FromArgb(cfg.SungAlpha, 255, 255, 255));
        _unsung = Frozen(Color.FromArgb(cfg.UnsungAlpha, 255, 255, 255));
        _bgBright = Frozen(Color.FromArgb(cfg.BgLineAlpha, 255, 255, 255));
        _bgDim = Frozen(Color.FromArgb((byte)(cfg.BgLineAlpha / 2), 255, 255, 255));
        _shadow = Frozen(Color.FromArgb(150, 0, 0, 0));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void SetLyrics(Lyrics? lyrics)
    {
        _lyrics = lyrics;
        _layoutLineIdx = -2;
        _mainLayout = null;
        _layoutBg = null;
        _bgLayout = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var st = ComputeState();
        if (!st.Visible) return;

        var ly = _lyrics!;
        var lines = ly.Lines;
        var t = st.T;
        var idx = st.Idx;
        var showDots = st.ShowDots;
        var gapStart = st.GapStart;
        var gapEnd = st.GapEnd;
        var activeBg = st.Bg;

        var availW = ActualWidth - 4;
        if (availW < 40) return;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // _bgAnim is advanced by Tick() so the slide keeps time even across
        // skipped frames; here we only shape it.
        var bgEase = _bgAnim * _bgAnim * (3 - 2 * _bgAnim); // smoothstep

        // Content is centered inside the strip so the overlay looks natural
        // in the taskbar's free space regardless of window alignment.
        if (showDots)
        {
            DrawDots(dc, t, gapStart, gapEnd);
            // fillers can overlap interludes ("yeah" during a break) — keep drawing bg below
        }
        else
        {
            var line = lines[idx];
            if (_layoutLineIdx != idx)
            {
                _mainLayout = LineLayout.Build(line.Text, line.Sylls, _typeface, _cfg.MainFontPx,
                    availW, pixelsPerDip, _sung, _unsung, _shadow);
                _layoutLineIdx = idx;
                _lineShownTick = Stopwatch.GetTimestamp();
            }
            if (_mainLayout is { } ml)
            {
                var fadeMs = (Stopwatch.GetTimestamp() - _lineShownTick) / (double)Stopwatch.Frequency * 1000;
                var fade = Math.Clamp(fadeMs / 160.0, 0, 1);

                double centerY = (ActualHeight - ml.Height) / 2;
                double topY = Math.Max(1, (ActualHeight - ml.Height - _cfg.BgFontPx * 1.45) / 2);
                double mainY = centerY + (topY - centerY) * bgEase;
                double mainX = Math.Max(2, (ActualWidth - ml.Width) / 2);

                if (fade < 1) dc.PushOpacity(0.35 + 0.65 * fade);
                ml.Draw(dc, mainX, mainY, SweepX(ml, line, t));
                if (fade < 1) dc.Pop();
            }
        }

        if (activeBg != null && !ReferenceEquals(_layoutBg, activeBg))
        {
            _bgLayout = LineLayout.Build(activeBg.Text, activeBg.Sylls, _typeface, _cfg.BgFontPx,
                availW * 0.9, pixelsPerDip, _bgBright, _bgDim, _shadow);
            _layoutBg = activeBg;
        }

        // Keep drawing the last filler while it slides out (activeBg may be null).
        if (_bgLayout is { } bl && _layoutBg is { } bgv && bgEase > 0.01)
        {
            double mlH = showDots ? 0 : _mainLayout?.Height ?? 0;
            double y = showDots
                ? (ActualHeight - bl.Height) / 2 + _cfg.MainFontPx * 0.55
                : Math.Max(1, (ActualHeight - mlH - bl.Height) / 2) + mlH + 1;
            y += (1 - bgEase) * 7; // slide up on entry, down on exit

            double x;
            if (bl.Segs != null)
            {
                x = 0;
                foreach (var s in bl.Segs)
                {
                    if (t >= s.End) x = s.X1;
                    else if (t >= s.Start) { x = s.X0 + (s.X1 - s.X0) * (t - s.Start) / Math.Max(1, s.End - s.Start); break; }
                    else break;
                }
            }
            else
            {
                x = t >= bgv.Start ? bl.Width : 0;
            }

            dc.PushOpacity(bgEase);
            bl.Draw(dc, Math.Max(6, (ActualWidth - bl.Width) / 2), y, x);
            dc.Pop();
        }
        else if (activeBg == null && _bgAnim <= 0.01)
        {
            _layoutBg = null;
            _bgLayout = null;
        }
    }

    private static double SweepX(LineLayout ml, LyricLine line, double t)
    {
        if (ml.Segs == null)
        {
            // Line-synced: whole line lights up while active, stays lit after.
            return t >= line.Start ? ml.Width + 2 : 0;
        }
        double x = 0;
        foreach (var s in ml.Segs)
        {
            if (t >= s.End) x = s.X1;
            else if (t >= s.Start)
            {
                x = s.X0 + (s.X1 - s.X0) * (t - s.Start) / Math.Max(1, s.End - s.Start);
                break;
            }
            else break;
        }
        return x;
    }

    private void DrawDots(DrawingContext dc, double t, double gapStart, double gapEnd)
    {
        var progress = Math.Clamp((t - gapStart) / Math.Max(1, gapEnd - gapStart), 0, 1);
        double cy = ActualHeight / 2;
        const double r = 3.2, gap = 15;
        double x0 = (ActualWidth - 2 * gap) / 2; // center the dot group
        for (int i = 0; i < 3; i++)
        {
            var f = Math.Clamp(progress * 3 - i, 0, 1);
            double cx = x0 + i * gap;
            dc.DrawEllipse(_unsung, null, new Point(cx, cy), r, r);
            if (f > 0)
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * f), 255, 255, 255)),
                    null, new Point(cx, cy), r, r);
        }
    }
}

/// <summary>Measured text + per-syllable x positions for one lyric line, built once per line.</summary>
public sealed class LineLayout
{
    public sealed record SegX(double Start, double End, double X0, double X1);

    public required FormattedText Bright;
    public required FormattedText Dim;
    public required FormattedText Shadow;
    public List<SegX>? Segs;
    public double Width;
    public double Height;

    public static LineLayout Build(string text, List<Seg>? sylls, Typeface tf, double fontPx,
        double maxW, double pixelsPerDip, Brush bright, Brush dim, Brush shadow)
    {
        FormattedText Make(string s, double size, Brush b) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, pixelsPerDip)
            { MaxLineCount = 1, Trimming = TextTrimming.None };

        var size = fontPx;
        var probe = Make(text, size, bright);
        if (probe.WidthIncludingTrailingWhitespace > maxW)
        {
            size = Math.Max(9, size * maxW / probe.WidthIncludingTrailingWhitespace * 0.98);
            probe = Make(text, size, bright);
        }

        var layout = new LineLayout
        {
            Bright = probe,
            Dim = Make(text, size, dim),
            Shadow = Make(text, size, shadow),
            Width = probe.WidthIncludingTrailingWhitespace,
            Height = probe.Height,
        };

        if (sylls is { Count: > 0 })
        {
            // Cumulative prefix widths give exact pixel spans per syllable.
            var segs = new List<SegX>(sylls.Count);
            var sb = new System.Text.StringBuilder();
            double prev = 0;
            foreach (var s in sylls)
            {
                sb.Append(s.Text);
                var w = Make(sb.ToString(), size, bright).WidthIncludingTrailingWhitespace;
                segs.Add(new SegX(s.Start, s.End, prev, w));
                prev = w;
            }
            layout.Segs = segs;
        }
        return layout;
    }

    public void Draw(DrawingContext dc, double x, double y, double sweepX)
    {
        dc.DrawText(Shadow, new Point(x, y + 1));
        dc.DrawText(Dim, new Point(x, y));
        if (sweepX > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x, y - 2, Math.Min(sweepX, Width + 2), Height + 4)));
            dc.DrawText(Bright, new Point(x, y));
            dc.Pop();
        }
    }
}
