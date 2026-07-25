using System.Windows.Media;

namespace TaskbarLyrics;

/// <summary>
/// Everything a preset needs to paint one frame. The drawing surface is the
/// lower-third window: y=0 is the top (highest above the taskbar), y=Height is
/// the screen bottom. The bottom <see cref="TaskbarHeight"/> DIPs are the strip
/// that shows through the transparent taskbar; everything above that "bleeds"
/// over the desktop (and is naturally occluded by any app window in front).
/// </summary>
public readonly struct VizFrame
{
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required double TaskbarHeight { get; init; }

    /// <summary>Log-spaced magnitudes, index 0 = bass … N-1 = treble, ~0..1.4.</summary>
    public required float[] Spectrum { get; init; }
    public required float Level { get; init; }
    public required float Bass { get; init; }

    public required AlbumPalette Palette { get; init; }
    public required double TimeSec { get; init; }
    public required double DtMs { get; init; }
}

/// <summary>A pluggable way to draw the audio spectrum. Stateless-ish; may cache brushes internally.</summary>
public interface IVisualizerPreset
{
    /// <summary>Stable id persisted in config and used by the tray radio menu.</summary>
    string Id { get; }
    /// <summary>Human label shown in the tray.</summary>
    string Name { get; }
    void Render(DrawingContext dc, in VizFrame f);
}

/// <summary>Registry of the built-in presets. Add a new preset here to make it selectable.</summary>
public static class VisualizerPresets
{
    public static readonly IReadOnlyList<IVisualizerPreset> All = new IVisualizerPreset[]
    {
        new GlowTaskbarPreset(),
        new EqualizerTaskbarPreset(),
        new EqualizerBleedPreset(),
        new PulseBleedPreset(),
        new WaveBleedPreset(),
    };

    public static IVisualizerPreset Default => All[2]; // equalizer-bleed

    public static IVisualizerPreset Get(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? Default;

    private static readonly Random Rng = new();

    /// <summary>Pick a preset different from <paramref name="currentId"/> when possible.</summary>
    public static IVisualizerPreset Random(string? currentId)
    {
        if (All.Count == 1) return All[0];
        IVisualizerPreset pick;
        do { pick = All[Rng.Next(All.Count)]; } while (pick.Id == currentId);
        return pick;
    }
}
