#pragma once

#include <QHash>
#include <QRect>
#include <QObject>
#include <QUrl>

class QQmlEngine;
class QQuickItem;
class QQuickWindow;
class QScreen;
struct Config;

/// One full-screen layer-shell surface per monitor, each a WebEngine view showing the
/// wallpaper page (wallpaper/index.html). It sits in the Bottom layer by default: above
/// the shell's own wallpaper (Background), below windows, ignoring exclusive zones, and
/// passing clicks through. With Hyprland's `misc:session_lock_xray` it also shows behind
/// a transparent lock screen, which is how the lock-screen layout is seen.
class WallpaperWindows : public QObject
{
    Q_OBJECT
public:
    WallpaperWindows(Config *cfg, QObject *parent = nullptr);
    ~WallpaperWindows() override;
    void start();
    void reload();
    /// The page's clickable regions on one monitor become the surface's input region;
    /// everywhere else stays click-through.
    void setHitboxes(const QString &screen, const QList<QRect> &rects);

    /// Called from QML with the page's console output.
    Q_INVOKABLE void console(const QString &screen, int level, const QString &message, int line, const QString &source);

private:
    void addScreen(QScreen *screen);
    void removeScreen(QScreen *screen);

    struct Surface {
        QQuickWindow *window = nullptr;
        QQuickItem *item = nullptr;
    };

    Config *m_cfg;
    QUrl m_url;
    QQmlEngine *m_engine = nullptr;
    QHash<QScreen *, Surface> m_surfaces;
};
