using System.IO;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace TaskbarLyrics;

/// <summary>
/// A small set of gradient stops pulled from album art. <see cref="A"/> is the
/// dominant vibrant color, <see cref="B"/>/<see cref="C"/> secondary hues.
/// </summary>
public sealed record AlbumPalette(Color A, Color B, Color C)
{
    /// <summary>Neutral, deep purple→blue→magenta used before art loads or on failure.</summary>
    public static readonly AlbumPalette Default = new(
        Color.FromRgb(0x4E, 0x1A, 0x82),
        Color.FromRgb(0x14, 0x52, 0x99),
        Color.FromRgb(0x8F, 0x22, 0x50));

    /// <summary>Extract a vibrant palette from encoded image bytes (JPEG/PNG). Never throws.</summary>
    public static AlbumPalette FromImageBytes(byte[]? bytes)
    {
        if (bytes is not { Length: > 64 }) return Default;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var src = new System.Drawing.Bitmap(ms);

            // Sample a coarse grid so cost is bounded regardless of art size.
            int step = Math.Max(1, Math.Max(src.Width, src.Height) / 48);
            const int Bins = 18;
            var wsum = new double[Bins];
            var rsum = new double[Bins];
            var gsum = new double[Bins];
            var bsum = new double[Bins];

            for (int y = 0; y < src.Height; y += step)
            for (int x = 0; x < src.Width; x += step)
            {
                var p = src.GetPixel(x, y);
                RgbToHsv(p.R, p.G, p.B, out double h, out double s, out double v);
                if (v < 0.16 || s < 0.18) continue;          // skip near-black / gray
                double w = s * v;                            // favor colorful & bright
                int bin = Math.Clamp((int)(h / 360.0 * Bins), 0, Bins - 1);
                wsum[bin] += w;
                rsum[bin] += p.R * w;
                gsum[bin] += p.G * w;
                bsum[bin] += p.B * w;
            }

            var ranked = Enumerable.Range(0, Bins)
                .Where(i => wsum[i] > 0)
                .OrderByDescending(i => wsum[i])
                .Take(3)
                .Select(i => Vibrant(
                    (byte)(rsum[i] / wsum[i]),
                    (byte)(gsum[i] / wsum[i]),
                    (byte)(bsum[i] / wsum[i])))
                .ToList();

            if (ranked.Count == 0) return Default;
            var a = ranked[0];
            var b = ranked.Count > 1 ? ranked[1] : Shift(a, 0.5);
            var c = ranked.Count > 2 ? ranked[2] : Shift(a, -0.5);
            return new AlbumPalette(a, b, c);
        }
        catch (Exception ex)
        {
            Log.Write($"viz-palette: extract failed: {ex.Message}");
            return Default;
        }
    }

    /// <summary>Keep a color richly saturated but deliberately deep/dim so it stays a backdrop.</summary>
    private static Color Vibrant(byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b, out double h, out double s, out double v);
        s = Math.Clamp(s * 1.2 + 0.1, 0, 1);
        v = Math.Clamp(v * 0.72, 0.14, 0.62);   // pull brightness down for readability
        HsvToRgb(h, s, v, out byte rr, out byte gg, out byte bb);
        return Color.FromRgb(rr, gg, bb);
    }

    /// <summary>Rotate a color's hue by <paramref name="turns"/> of the wheel (for synthetic stops).</summary>
    private static Color Shift(Color c, double turns)
    {
        RgbToHsv(c.R, c.G, c.B, out double h, out double s, out double v);
        h = (h + turns * 60 + 360) % 360;
        HsvToRgb(h, s, v, out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == rd) h = 60 * (((gd - bd) / d) % 6);
        else if (max == gd) h = 60 * (((bd - rd) / d) + 2);
        else h = 60 * (((rd - gd) / d) + 4);
        if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = v - c;
        double rd, gd, bd;
        if (h < 60) (rd, gd, bd) = (c, x, 0);
        else if (h < 120) (rd, gd, bd) = (x, c, 0);
        else if (h < 180) (rd, gd, bd) = (0, c, x);
        else if (h < 240) (rd, gd, bd) = (0, x, c);
        else if (h < 300) (rd, gd, bd) = (x, 0, c);
        else (rd, gd, bd) = (c, 0, x);
        r = (byte)Math.Clamp((rd + m) * 255, 0, 255);
        g = (byte)Math.Clamp((gd + m) * 255, 0, 255);
        b = (byte)Math.Clamp((bd + m) * 255, 0, 255);
    }
}
