#pragma once

#include <QJsonObject>
#include <QString>

/// One wallpaper surface's look. Keys mirror the page's `settings` (wallpaper/js/app.js);
/// sent as-is and applied over its config.js defaults.
struct WallpaperSettings {
    QString background = QStringLiteral("wallpapers"); // "wallpapers" | "dynamic"
    QString collection = QStringLiteral("all");
    QString layout = QStringLiteral("split");          // "split" | "lyrics" | "lock"
    int lyricsSize = 100;  // %
    int dim = 35;          // %
    int wallpaperBlur = 0; // px
    bool kenBurns = true;
    bool audioReactive = true;
    bool lineBlur = true;
    bool spicyFont = true;
    bool clock = true;

    static WallpaperSettings lockDefaults();
    QJsonObject toMessage() const;
    QJsonObject toJson() const;
    static WallpaperSettings fromJson(const QJsonObject &o, const WallpaperSettings &defaults);
};

/// ~/.config/spicy-wallpaper/config.json, created with defaults on first run. The tray
/// writes it back on every change.
struct Config {
    int port = 9012;
    int globalOffsetMs = 0;          // positive = lyrics earlier
    bool interactive = true;         // clickable lyrics/cover/controls (desktop + lock relay)
    QString layer = QStringLiteral("bottom"); // layer-shell layer: "background" | "bottom"
    QString wallpaperDir;            // folder holding index.html; empty = auto
    QString wallpaperFolder;         // images, one picked per song change
    WallpaperSettings desktop;
    WallpaperSettings lock = WallpaperSettings::lockDefaults();

    static Config load();
    void save() const;
    static QString path();

    /// The resolved wallpaper page folder (contains index.html).
    QString resolveWallpaperDir() const;
};
