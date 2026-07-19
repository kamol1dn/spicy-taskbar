using System.Diagnostics;

namespace TaskbarLyrics;

/// <summary>
/// Holds the latest known playback position and interpolates between updates.
/// Written by the SMTC watcher (or the Spotify bridge), read at render time.
/// </summary>
public static class PositionEngine
{
    private sealed record Snap(double BaseMs, long Tick, bool Playing, double Rate);

    private static volatile Snap? _snap;

    public static void Set(double positionMs, bool playing, double rate = 1.0) =>
        _snap = new Snap(positionMs, Stopwatch.GetTimestamp(), playing, rate <= 0 ? 1.0 : rate);

    public static void Clear() => _snap = null;

    public static bool HasPosition => _snap != null;
    public static bool Playing => _snap?.Playing ?? false;

    /// <summary>Current interpolated position in ms, or null when nothing is playing.</summary>
    public static double? NowMs
    {
        get
        {
            var s = _snap;
            if (s == null) return null;
            if (!s.Playing) return s.BaseMs;
            var elapsed = (Stopwatch.GetTimestamp() - s.Tick) / (double)Stopwatch.Frequency * 1000.0;
            return s.BaseMs + elapsed * s.Rate;
        }
    }
}
