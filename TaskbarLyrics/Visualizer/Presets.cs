using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace TaskbarLyrics;

/// <summary>Shared brush cache + geometry helpers for the presets.</summary>
internal static class VizPaint
{
    private static AlbumPalette? _cachedPal;
    private static double _cachedW;
    private static LinearGradientBrush? _cachedH;

    /// <summary>Full-width horizontal album gradient (absolute-mapped, frozen, cached per song).</summary>
    public static LinearGradientBrush Horizontal(AlbumPalette p, double w)
    {
        if (_cachedH != null && _cachedPal == p && Math.Abs(_cachedW - w) < 0.5) return _cachedH;
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(w, 0),
            MappingMode = BrushMappingMode.Absolute,
        };
        b.GradientStops.Add(new GradientStop(p.A, 0.0));
        b.GradientStops.Add(new GradientStop(p.B, 0.5));
        b.GradientStops.Add(new GradientStop(p.C, 1.0));
        b.Freeze();
        _cachedH = b; _cachedPal = p; _cachedW = w;
        return b;
    }

    /// <summary>
    /// Vertical opacity mask: fully opaque from <paramref name="bottom"/> up to
    /// <paramref name="solidTop"/>, then fading to transparent by
    /// <paramref name="fadeTop"/>. Used so bleed presets dissolve into the desktop.
    /// </summary>
    public static Brush VerticalFade(double bottom, double solidTop, double fadeTop)
    {
        double span = bottom - fadeTop;
        double solidOff = span <= 0 ? 0 : Math.Clamp((bottom - solidTop) / span, 0, 1);
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, bottom),
            EndPoint = new Point(0, fadeTop),
            MappingMode = BrushMappingMode.Absolute,
        };
        var opaque = Color.FromArgb(255, 255, 255, 255);
        var clear = Color.FromArgb(0, 255, 255, 255);
        b.GradientStops.Add(new GradientStop(opaque, 0));
        b.GradientStops.Add(new GradientStop(opaque, solidOff));
        b.GradientStops.Add(new GradientStop(clear, 1));
        b.Freeze();
        return b;
    }

    public static float Amp(float[] spec, int i) => Math.Clamp(spec[i], 0, 1f);

    /// <summary>Filled area under the spectrum curve, from the top edge down to the window bottom.</summary>
    public static Geometry SpectrumArea(in VizFrame f, double baseline, double maxAmp, bool smooth)
    {
        var spec = f.Spectrum;
        int n = spec.Length;
        double w = f.Width, bottom = f.Height;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, bottom), true, true);
            Point prev = new(0, baseline - Amp(spec, 0) * maxAmp);
            ctx.LineTo(prev, true, smooth);
            for (int i = 1; i < n; i++)
            {
                double x = w * i / (n - 1);
                double y = baseline - Amp(spec, i) * maxAmp;
                if (smooth)
                {
                    double mx = (prev.X + x) / 2;
                    ctx.QuadraticBezierTo(new Point(prev.X, prev.Y), new Point(mx, (prev.Y + y) / 2), true, true);
                }
                ctx.LineTo(new Point(x, y), true, smooth);
                prev = new Point(x, y);
            }
            ctx.LineTo(new Point(w, bottom), true, false);
        }
        g.Freeze();
        return g;
    }
}

// ---- Taskbar-confined presets --------------------------------------------

/// <summary>Smooth gradient glow that lives entirely inside the transparent taskbar strip.</summary>
public sealed class GlowTaskbarPreset : IVisualizerPreset
{
    public string Id => "glow-taskbar";
    public string Name => "Glow — taskbar only";

    public void Render(DrawingContext dc, in VizFrame f)
    {
        double strip = f.TaskbarHeight;
        if (strip < 6) return;
        double bottom = f.Height;
        double baseline = bottom - strip * 0.12;
        double maxAmp = strip * (0.55 + 0.45 * Math.Clamp(f.Level, 0, 1));

        var area = VizPaint.SpectrumArea(f, baseline, maxAmp, smooth: true);
        dc.PushOpacityMask(VizPaint.VerticalFade(bottom, bottom - strip * 0.15, bottom - strip));
        dc.PushOpacity(0.5 + 0.5 * Math.Clamp(f.Level, 0, 1));
        dc.DrawGeometry(VizPaint.Horizontal(f.Palette, f.Width), null, area);
        dc.Pop();
        dc.Pop();
    }
}

/// <summary>Classic bars, clipped to the taskbar strip so nothing bleeds above it.</summary>
public sealed class EqualizerTaskbarPreset : IVisualizerPreset
{
    public string Id => "equalizer-taskbar";
    public string Name => "Equalizer — taskbar only";

    public void Render(DrawingContext dc, in VizFrame f)
    {
        double strip = f.TaskbarHeight;
        if (strip < 6) return;
        Bars.Draw(dc, f, baseline: f.Height, maxAmp: strip * 0.92,
            solidTop: f.Height - strip, fadeTop: f.Height - strip);
    }
}

// ---- Bleed presets --------------------------------------------------------

/// <summary>Bars that rise out of the taskbar into the lower third, fading as they climb.</summary>
public sealed class EqualizerBleedPreset : IVisualizerPreset
{
    public string Id => "equalizer-bleed";
    public string Name => "Equalizer — bleed";

    public void Render(DrawingContext dc, in VizFrame f)
    {
        double maxAmp = f.Height * 0.9;
        Bars.Draw(dc, f, baseline: f.Height, maxAmp: maxAmp,
            solidTop: f.Height - f.TaskbarHeight, fadeTop: f.Height - maxAmp);
    }
}

/// <summary>Bass-pumping gradient slab that swells up out of the taskbar on every kick.</summary>
public sealed class PulseBleedPreset : IVisualizerPreset
{
    public string Id => "pulse-bleed";
    public string Name => "Pulse — bleed";

    public void Render(DrawingContext dc, in VizFrame f)
    {
        double bottom = f.Height;
        double energy = Math.Clamp(0.12 + f.Bass * 0.95 + f.Level * 0.25, 0, 1);
        double h = f.TaskbarHeight + (f.Height - f.TaskbarHeight) * energy;
        double top = bottom - h;

        var rect = new Rect(0, top, f.Width, h);
        dc.PushOpacityMask(VizPaint.VerticalFade(bottom, bottom - f.TaskbarHeight * 0.5, top));
        dc.PushOpacity(0.45 + 0.55 * energy);
        dc.DrawRectangle(VizPaint.Horizontal(f.Palette, f.Width), null, rect);
        dc.Pop();
        dc.Pop();

        // Bright horizontal core that flares on the beat.
        double coreH = f.TaskbarHeight * (0.5 + f.Bass * 0.9);
        var core = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 1),
            Center = new Point(0.5, 1),
            RadiusX = 0.75, RadiusY = 1,
        };
        core.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(110 * energy), 255, 240, 245), 0));
        core.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 240, 245), 1));
        core.Freeze();
        dc.DrawRectangle(core, null, new Rect(0, bottom - coreH, f.Width, coreH));
    }
}

/// <summary>Flowing, mirrored waveform band that ripples up above the taskbar.</summary>
public sealed class WaveBleedPreset : IVisualizerPreset
{
    public string Id => "wave-bleed";
    public string Name => "Wave — bleed";

    public void Render(DrawingContext dc, in VizFrame f)
    {
        double maxAmp = f.Height * 0.75;
        double baseline = f.Height - f.TaskbarHeight * 0.4;

        var area = VizPaint.SpectrumArea(f, baseline, maxAmp, smooth: true);
        dc.PushOpacityMask(VizPaint.VerticalFade(f.Height, f.Height - f.TaskbarHeight, f.Height - maxAmp));
        dc.PushOpacity(0.85);
        dc.DrawGeometry(VizPaint.Horizontal(f.Palette, f.Width), null, area);
        dc.Pop();
        dc.Pop();
    }
}

/// <summary>Shared bar renderer for the equalizer presets.</summary>
internal static class Bars
{
    public static void Draw(DrawingContext dc, in VizFrame f, double baseline, double maxAmp,
        double solidTop, double fadeTop)
    {
        var spec = f.Spectrum;
        int n = spec.Length;
        double slot = f.Width / n;
        double gap = Math.Min(slot * 0.28, 3);
        double bw = Math.Max(1, slot - gap);
        double r = Math.Min(bw / 2, 3);
        var brush = VizPaint.Horizontal(f.Palette, f.Width);

        dc.PushOpacityMask(VizPaint.VerticalFade(f.Height, solidTop, fadeTop));
        for (int i = 0; i < n; i++)
        {
            double h = VizPaint.Amp(spec, i) * maxAmp;
            if (h < 1) continue;
            double x = i * slot + gap / 2;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, baseline - h, bw, h), r, r);
        }
        dc.Pop();
    }
}
