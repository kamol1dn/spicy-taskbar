using System.Text;
using System.Text.Json;

namespace TaskbarLyrics;

/// <summary>One timed text segment (a syllable, or a whole word chunk). Times in ms.</summary>
public sealed record Seg(double Start, double End, string Text, bool PartOfWord);

/// <summary>A background/filler vocal ("yeah", "come on", echoes) attached near a main line.</summary>
public sealed class BgVocal
{
    public double Start;
    public double End;
    public string Text = "";
    public List<Seg>? Sylls; // null => no syllable timing, sweep linearly
}

public sealed class LyricLine
{
    public double Start;
    public double End;
    public string Text = "";
    public List<Seg>? Sylls; // null => line-synced only
}

public sealed class Lyrics
{
    public string Type = "Line";      // Syllable | Line
    public double StartTime;          // ms, when the first vocal starts
    public List<LyricLine> Lines = new();
    public List<BgVocal> Bg = new();  // flattened, sorted by Start
    public string Source = "";
}

public static class LyricsNormalizer
{
    /// <summary>Convert the SpicyLyrics API JSON (seconds) into our model (ms). Returns null for Static/unusable.</summary>
    public static Lyrics? FromSpicy(JsonElement root)
    {
        var type = root.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
        if (type != "Syllable" && type != "Line") return null; // Static = unsynced, nothing to show

        var ly = new Lyrics { Type = type, Source = "spicy" };
        if (root.TryGetProperty("StartTime", out var st) && st.ValueKind == JsonValueKind.Number)
            ly.StartTime = st.GetDouble() * 1000;

        if (!root.TryGetProperty("Content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (type == "Line")
            {
                var line = new LyricLine
                {
                    Text = item.GetPropOr("Text", ""),
                    Start = item.GetPropOr("StartTime", 0.0) * 1000,
                    End = item.GetPropOr("EndTime", 0.0) * 1000,
                };
                if (!string.IsNullOrWhiteSpace(line.Text)) ly.Lines.Add(line);
                continue;
            }

            // Syllable type: { Lead: {StartTime, EndTime, Syllables[]}, Background?: [...] }
            if (item.TryGetProperty("Lead", out var lead))
            {
                var (text, segs) = ReadSyllables(lead);
                if (text.Length > 0)
                {
                    ly.Lines.Add(new LyricLine
                    {
                        Text = text,
                        Start = lead.GetPropOr("StartTime", 0.0) * 1000,
                        End = lead.GetPropOr("EndTime", 0.0) * 1000,
                        Sylls = segs,
                    });
                }
            }
            if (item.TryGetProperty("Background", out var bgs) && bgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var bg in bgs.EnumerateArray())
                {
                    var (text, segs) = ReadSyllables(bg);
                    if (text.Length == 0) continue;
                    ly.Bg.Add(new BgVocal
                    {
                        Text = text,
                        Start = bg.GetPropOr("StartTime", 0.0) * 1000,
                        End = bg.GetPropOr("EndTime", 0.0) * 1000,
                        Sylls = segs,
                    });
                }
            }
        }

        ly.Lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        ly.Bg.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (ly.Lines.Count == 0) return null;
        if (ly.StartTime <= 0) ly.StartTime = ly.Lines[0].Start;
        return ly;
    }

    private static (string, List<Seg>) ReadSyllables(JsonElement holder)
    {
        var segs = new List<Seg>();
        var sb = new StringBuilder();
        if (holder.TryGetProperty("Syllables", out var sylls) && sylls.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sylls.EnumerateArray())
            {
                var text = s.GetPropOr("Text", "");
                var part = s.TryGetProperty("IsPartOfWord", out var p) && p.ValueKind == JsonValueKind.True;
                var seg = new Seg(
                    s.GetPropOr("StartTime", 0.0) * 1000,
                    s.GetPropOr("EndTime", 0.0) * 1000,
                    text + (part ? "" : " "),
                    part);
                segs.Add(seg);
                sb.Append(seg.Text);
            }
        }
        return (sb.ToString().TrimEnd(), segs);
    }

    /// <summary>Parse an LRC string (from LRCLIB) into line-synced lyrics.</summary>
    public static Lyrics? FromLrc(string lrc, double durationMs)
    {
        var entries = new List<(double ms, string text)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = System.Text.RegularExpressions.Regex.Matches(line, @"\[(\d+):(\d+)(?:[.:](\d+))?\]");
            if (matches.Count == 0) continue;
            var text = line[(matches[^1].Index + matches[^1].Length)..].Trim();
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                double ms = int.Parse(m.Groups[1].Value) * 60_000 + int.Parse(m.Groups[2].Value) * 1000;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms += frac.Length == 2 ? int.Parse(frac) * 10 : int.Parse(frac.PadRight(3, '0')[..3]);
                }
                entries.Add((ms, text));
            }
        }
        entries.Sort((a, b) => a.ms.CompareTo(b.ms));
        if (entries.Count == 0) return null;

        var ly = new Lyrics { Type = "Line", Source = "lrclib" };
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(entries[i].text)) continue; // empty stamps mark gaps
            double end = i + 1 < entries.Count ? entries[i + 1].ms
                       : durationMs > entries[i].ms ? durationMs : entries[i].ms + 6000;
            ly.Lines.Add(new LyricLine { Text = entries[i].text, Start = entries[i].ms, End = end });
        }
        if (ly.Lines.Count == 0) return null;
        ly.StartTime = ly.Lines[0].Start;
        return ly;
    }

    private static string GetPropOr(this JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static double GetPropOr(this JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}
