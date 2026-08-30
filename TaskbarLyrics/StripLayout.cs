using System.Windows;

namespace TaskbarLyrics;

/// <summary>
/// Screen geometry shared by the strip-shaped overlays (lyrics, app name).
/// Keeps the "which edge, inside the taskbar or floating, which side" rules in
/// one place so every module positions itself identically.
/// </summary>
public static class StripLayout
{
    /// <summary>
    /// Where a strip of at most <paramref name="maxWidth"/> should sit.
    /// <paramref name="preferredHeight"/> is used as-is when floating, and as a
    /// cap when sitting inside the taskbar band.
    /// </summary>
    public static Rect Compute(string vpos, string placement, string align, int xOffset,
        double maxWidth, double preferredHeight)
    {
        var wa = SystemParameters.WorkArea;                 // DIPs, excludes taskbar
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        var atTop = vpos == "top";
        // Taskbar thickness on the edge we're hugging (0 when the bar lives elsewhere).
        var barH = atTop ? wa.Top : screenH - wa.Bottom;

        double h, top;
        if (placement == "taskbar" && barH >= 24)
        {
            // Sit inside the taskbar band on this edge.
            h = Math.Min(barH - 2, preferredHeight);
            top = atTop ? (barH - h) / 2 : wa.Bottom + (barH - h) / 2;
        }
        else
        {
            // Floating strip just inside the work area on this edge.
            h = preferredHeight;
            top = atTop ? wa.Top + 6 : wa.Bottom - h - 6;
        }

        var w = Math.Min(maxWidth, screenW * 0.62);
        double left = align switch
        {
            "center" => wa.Left + (screenW - w) / 2,
            "right" => wa.Left + screenW - w - xOffset,
            _ => wa.Left + xOffset,
        };
        return new Rect(left, top, w, h);
    }

    /// <summary>Move/resize only when it would actually differ.</summary>
    public static void Apply(Window win, Rect r)
    {
        if (Math.Abs(win.Left - r.X) > 0.5 || Math.Abs(win.Top - r.Y) > 0.5 ||
            Math.Abs(win.Width - r.Width) > 0.5 || Math.Abs(win.Height - r.Height) > 0.5)
        {
            win.Left = r.X; win.Top = r.Y; win.Width = r.Width; win.Height = r.Height;
        }
    }

    /// <summary>
    /// X of a <paramref name="contentW"/>-wide row inside a strip of width
    /// <paramref name="availW"/>, honouring the alignment. <paramref name="pad"/>
    /// is the edge inset, and the floor when the content is wider than the strip.
    /// </summary>
    public static double AlignX(string align, double contentW, double availW, double pad) => align switch
    {
        "left" => pad,
        "right" => Math.Max(pad, availW - contentW - pad),
        _ => Math.Max(pad, (availW - contentW) / 2),
    };

    /// <summary>Normalizes a stored/entered value to "top" or "bottom".</summary>
    public static string NormVPos(string? v) => v == "top" ? "top" : "bottom";

    /// <summary>Normalizes a stored/entered value to "left", "center" or "right".</summary>
    public static string NormAlign(string? v) => v is "left" or "center" or "right" ? v : "center";
}
