using System.Text.RegularExpressions;

namespace TaskbarLyrics;

/// <summary>
/// Cleans messy media metadata (YouTube video titles especially) into
/// (artist, title) candidates and scores Spotify search results against them.
/// </summary>
public static partial class Matching
{
    [GeneratedRegex(@"[(\[][^)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|m\/?v|hd|hq|4k|remaster(ed)?|live|color coded|performance|explicit|clean|full)\b[^)\]]*[)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex JunkGroups();

    [GeneratedRegex(@"\s*(\||//).*$")]
    private static partial Regex TrailingPipe();

    [GeneratedRegex(@"\s*[(\[]\b(ft\.?|feat\.?|featuring|with)\b[^)\]]*[)\]]|\s*\b(ft\.?|feat\.?|featuring)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatPart();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex Punct();

    public static string CleanTitle(string title)
    {
        var t = JunkGroups().Replace(title, " ");
        t = TrailingPipe().Replace(t, "");
        return Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '–', '—');
    }

    public static string CleanArtist(string artist)
    {
        var a = artist.Trim();
        if (a.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) a = a[..^8]; // YT auto-channels
        a = Regex.Replace(a, @"VEVO$", "", RegexOptions.IgnoreCase);
        return a.Trim();
    }

    /// <summary>
    /// False for media with nothing to identify a song by: no artist and no
    /// "Artist - Title" in the title. Browser tabs look like this (an Instagram
    /// reel reports just "Instagram"), and a title-only search then matched an
    /// unrelated song that happens to share the page's name.
    /// </summary>
    public static bool IsIdentifiable(string rawTitle, string rawArtist) =>
        CleanArtist(rawArtist).Length > 0 ||
        Regex.IsMatch(CleanTitle(rawTitle), @"^(.{1,60}?)\s*[-–—]\s+(.+)$");

    /// <summary>Best-first list of (artist, title) interpretations of the metadata.</summary>
    public static List<(string Artist, string Title)> Candidates(string rawTitle, string rawArtist)
    {
        var list = new List<(string, string)>();
        var title = CleanTitle(rawTitle);
        var artist = CleanArtist(rawArtist);

        // "Artist - Title" embedded in the video title beats the channel name.
        var m = Regex.Match(title, @"^(.{1,60}?)\s*[-–—]\s+(.+)$");
        if (m.Success)
            list.Add((m.Groups[1].Value.Trim(), FeatPart().Replace(m.Groups[2].Value, " ").Trim()));

        if (artist.Length > 0)
            list.Add((artist, FeatPart().Replace(title, " ").Trim()));
        list.Add(("", title));

        return list.Where(c => c.Item2.Length > 0).Distinct().ToList();
    }

    public static string Norm(string s) =>
        Regex.Replace(Punct().Replace(s.ToLowerInvariant(), " "), @"\s+", " ").Trim();

    private static double TokenSim(string a, string b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        var ta = na.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        double inter = ta.Intersect(tb).Count();
        var sim = inter / Math.Max(ta.Count, tb.Count);

        // Squashed comparison rescues space-less names: "BillieEilish" (VEVO
        // channel) vs "Billie Eilish" tokenizes to zero overlap but is the
        // same string without spaces.
        if (sim < 1)
        {
            var sa = na.Replace(" ", "");
            var sb = nb.Replace(" ", "");
            if (sa.Length > 2 && sb.Length > 2 && (sa == sb || sa.Contains(sb) || sb.Contains(sa)))
                sim = Math.Max(sim, 0.9);
        }
        return sim;
    }

    /// <summary>Score a Spotify result against a metadata candidate. ~[0..1.35].</summary>
    public static double Score(TrackCandidate track, (string Artist, string Title) cand, double durationMs)
    {
        var titleSim = TokenSim(track.Name, cand.Title);
        // containment bonus: "Blinding Lights" inside "Blinding Lights (Remix)" etc.
        var nName = Norm(track.Name);
        var nTitle = Norm(cand.Title);
        if (nName.Length > 0 && nTitle.Length > 0 && (nName.Contains(nTitle) || nTitle.Contains(nName)))
            titleSim = Math.Max(titleSim, 0.85);

        double artistSim = 0;
        if (cand.Artist.Length > 0)
            artistSim = track.Artists.Count == 0 ? 0
                : track.Artists.Max(a => TokenSim(a, cand.Artist));

        // Duration is a boost when it agrees but only a mild penalty when it
        // doesn't — YouTube music videos carry intro/outro padding, so their
        // reported duration routinely disagrees with the studio track's.
        double durBonus = 0;
        if (durationMs > 1000 && track.DurationMs > 0)
        {
            var diff = Math.Abs(track.DurationMs - durationMs);
            durBonus = diff < 3000 ? 0.35 : diff < 8000 ? 0.2 : diff < 25000 ? 0 : -0.15;
        }

        return titleSim * 0.6 + artistSim * 0.4 + durBonus;
    }

    public static TrackCandidate? PickBest(
        List<TrackCandidate> results, List<(string Artist, string Title)> cands, double durationMs,
        out double bestScore)
    {
        TrackCandidate? best = null;
        bestScore = 0;
        foreach (var track in results)
            foreach (var cand in cands)
            {
                var s = Score(track, cand, durationMs);
                if (s > bestScore) { bestScore = s; best = track; }
            }
        return bestScore >= 0.55 ? best : null;
    }
}
