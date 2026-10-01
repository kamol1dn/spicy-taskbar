// spicy-wallpaper — the Linux port of TaskbarLyrics' wallpaper half.
//
// One process: the spicetify bridge + lyrics resolver (as on Windows), MPRIS in place of
// the Windows media session, cava in place of WASAPI loopback, logind's LockedHint for
// the lock screen, a tray icon, and the wallpaper page itself hosted in a WebEngine
// layer-shell surface per monitor (in place of Wallpaper Engine / Aura).

#include "BridgeServer.h"
#include "Common.h"
#include "Config.h"
#include "LockWatcher.h"
#include "LyricsService.h"
#include "MprisWatcher.h"
#include "Tray.h"
#include "WallpaperFeed.h"
#include "WallpaperWindows.h"

#include <QApplication>
#include <QDir>
#include <QLockFile>
#include <QStandardPaths>
#include <QtWebEngineQuick/qtwebenginequickglobal.h>
#include <QTimer>
#include <csignal>

int main(int argc, char *argv[])
{
    // Layer shell only exists on Wayland.
    if (qEnvironmentVariableIsEmpty("QT_QPA_PLATFORM")) qputenv("QT_QPA_PLATFORM", "wayland");
    QtWebEngineQuick::initialize(); // before the application object
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("spicy-wallpaper"));
    QApplication::setDesktopFileName(QStringLiteral("spicy-wallpaper"));
    QApplication::setQuitOnLastWindowClosed(false);

    QDir().mkpath(Paths::stateDir());
    QLockFile lock(Paths::stateDir() + QStringLiteral("/instance.lock"));
    if (!lock.tryLock(100)) {
        Log::write(QStringLiteral("already running; exiting"));
        return 0;
    }

    // Quit cleanly on SIGTERM/SIGINT (systemd stop, Ctrl+C) so WebEngine shuts down tidily.
    for (int sig : {SIGTERM, SIGINT}) std::signal(sig, [](int) { QMetaObject::invokeMethod(qApp, &QCoreApplication::quit, Qt::QueuedConnection); });

    Config cfg = Config::load();
    Log::write(QStringLiteral("---- starting ----"));

    BridgeServer bridge(cfg.port);
    MprisWatcher watcher(&bridge);   // subscribes to bridge state before the feed does
    LyricsService service(&bridge);
    WallpaperFeed feed(&bridge, &cfg);
    LockWatcher lockWatcher;
    WallpaperWindows windows(&cfg);

    std::optional<TrackInfo> lastInfo;
    QObject::connect(&watcher, &MprisWatcher::trackChanged, [&](const std::optional<TrackInfo> &info) {
        lastInfo = info;
        feed.onTrackChanged(info);
        service.onTrackChanged(info);
    });
    QObject::connect(&watcher, &MprisWatcher::artworkChanged, &feed, &WallpaperFeed::onArtwork);
    QObject::connect(&service, &LyricsService::lyricsResolved, &feed, &WallpaperFeed::onLyrics);
    QObject::connect(&lockWatcher, &LockWatcher::lockedChanged, &feed, &WallpaperFeed::setLocked);
    QObject::connect(&feed, &WallpaperFeed::controlRequested, &watcher, &MprisWatcher::control);
    QObject::connect(&feed, &WallpaperFeed::hitboxesChanged, &windows, &WallpaperWindows::setHitboxes);

    // The extension often connects a beat after startup (or after a Spotify restart).
    // Re-resolve the current track then, so a fallback result can upgrade to word-level
    // lyrics; the page keeps the old lines until the better ones land.
    QObject::connect(&bridge, &BridgeServer::clientConnected, [&] {
        QTimer::singleShot(1500, &service, [&] { // let sp_state arrive first
            if (lastInfo) service.onTrackChanged(lastInfo);
        });
    });

    bridge.start();
    watcher.start();
    lockWatcher.start();
    feed.setLocked(lockWatcher.locked());
    windows.start();
    Tray tray(&cfg, &feed, &windows);

    const int rc = app.exec();
    Log::write(QStringLiteral("---- exiting ----"));
    return rc;
}
