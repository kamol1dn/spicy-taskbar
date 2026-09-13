using System.Windows;

namespace TaskbarLyrics;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"unhandled: {e.Exception}");
            e.Handled = true;
        };

        var cfg = Config.Load();
        Log.Write("---- starting ----");

        var bridge = new BridgeServer(cfg.Port);
        var service = new LyricsService(bridge);
        var watcher = new SmtcWatcher(bridge);
        var overlay = new OverlayWindow(cfg);
        var viz = new VisualizerWindow(cfg);
        var appName = new AppNameWindow(cfg);
        // Feeds the Wallpaper Engine wallpaper (wallpaper/ folder) over ws://localhost:PORT/wallpaper.
        var feed = new WallpaperFeed(bridge, cfg);

        TrackInfo? lastInfo = null;
        watcher.TrackChanged += info =>
        {
            lastInfo = info;
            // Clear immediately so the previous song's lines never linger.
            overlay.Dispatcher.BeginInvoke(() => overlay.SetLyrics(null));
            viz.Dispatcher.BeginInvoke(viz.OnTrackChanged);
            feed.OnTrackChanged(info);
            service.OnTrackChanged(info);
        };
        // Album art drives the visualizer's gradient colors.
        watcher.ArtworkChanged += bytes =>
        {
            viz.Dispatcher.BeginInvoke(() => viz.SetArtwork(bytes));
            feed.OnArtwork(bytes);
        };
        service.LyricsResolved += lyrics =>
        {
            overlay.Dispatcher.BeginInvoke(() => overlay.SetLyrics(lyrics));
            feed.OnLyrics(lyrics);
        };

        // The extension often connects a beat after startup (or after a Spotify
        // restart). Re-resolve the current track then, so a fallback result can
        // upgrade to word-level lyrics; the overlay keeps showing the old ones
        // until the better ones land.
        bridge.ClientConnected += async () =>
        {
            await Task.Delay(1500); // let sp_state arrive first
            if (lastInfo is { } cur) service.OnTrackChanged(cur);
        };

        bridge.Start();
        watcher.Start();
        overlay.Show();
        viz.SetEnabled(cfg.VizEnabled);
        appName.SetEnabled(cfg.AppNameEnabled);

        using var tray = new TrayIcon(cfg, overlay, viz, appName, feed, app);
        app.Run();
    }
}
