#pragma once

#include <QObject>
#include <functional>

class QMenu;
class QSystemTrayIcon;
class WallpaperFeed;
class WallpaperWindows;
struct Config;
struct WallpaperSettings;

/// The status-notifier (tray) icon: wallpaper look per surface, image folder, lyrics
/// timing, log, cache, exit. Every change is saved to config.json and pushed to the pages.
class Tray : public QObject
{
    Q_OBJECT
public:
    Tray(Config *cfg, WallpaperFeed *feed, WallpaperWindows *windows, QObject *parent = nullptr);
    ~Tray() override;

private:
    void rebuild();
    void changed();
    void addSurfaceMenu(QMenu *parent, const QString &label, WallpaperSettings &s);

    Config *m_cfg;
    WallpaperFeed *m_feed;
    WallpaperWindows *m_windows;
    QSystemTrayIcon *m_icon = nullptr;
    QMenu *m_menu = nullptr;
};
