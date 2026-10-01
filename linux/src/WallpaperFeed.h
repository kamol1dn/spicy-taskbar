#pragma once

#include "Common.h"

#include <QDateTime>
#include <QJsonArray>
#include <QJsonObject>
#include <QRect>
#include <QObject>
#include <QProcess>
#include <QSet>
#include <QTimer>
#include <optional>

class BridgeServer;
class QWebSocket;
struct Config;

/// Mirrors track, lyrics, artwork and playback position to every wallpaper page over the
/// bridge's "/wallpaper" socket. Pages get a full snapshot when they connect, then live
/// updates; position goes out 4x a second and the page interpolates between pushes.
///
/// It also stands in for the Wallpaper Engine APIs the page would otherwise use:
///   {type:"list-wallpapers", dir} -> {type:"wallpapers", dir, files[]}
///   {type:"audio", on}            -> {type:"audio", bass} ~30x/s (from cava)
/// and carries the tray's settings ({type:"settings"}), "next wallpaper", and whether the
/// session is locked ({type:"surface", surface}), which flips the page's layout.
class WallpaperFeed : public QObject
{
    Q_OBJECT
public:
    WallpaperFeed(BridgeServer *bridge, Config *cfg, QObject *parent = nullptr);
    ~WallpaperFeed() override;

    void onTrackChanged(const std::optional<TrackInfo> &info);
    void onLyrics(const QJsonObject &lyrics);
    void onArtwork(const QString &url);
    void setLocked(bool locked);

    void pushSettings();
    void nextWallpaper();
    /// Collections offered in the tray: subfolders and filename prefixes
    /// ("nord_a_forest.jpg" -> "nord") holding at least 3 images, largest first.
    QStringList categories();

signals:
    /// A page asked for playback control; positionMs is already a player position.
    void controlRequested(const QString &action, double positionMs);
    /// A page's clickable regions changed (logical px on that screen).
    void hitboxesChanged(const QString &screen, const QList<QRect> &rects);

private:
    void sendSnapshot(QWebSocket *v);
    void onViewerMessage(QWebSocket *v, const QJsonObject &m);
    void onInputMessage(QWebSocket *input, const QJsonObject &m);
    QJsonObject positionMessage() const;
    QJsonObject settingsMessage() const;
    QJsonObject surfaceMessage() const;
    void pushPosition();
    void updateAudio();
    void readCava();
    static QStringList listImageFiles(const QString &dir);

    BridgeServer *m_bridge;
    Config *m_cfg;
    QTimer m_posTimer;
    QJsonObject m_track, m_lyrics, m_art;
    bool m_lastPlaying = false;
    bool m_locked = false;

    QString m_catDir;
    QDateTime m_catAt;
    QStringList m_catNames;

    QHash<QString, QJsonArray> m_hitboxes; // screen -> rects, as the page sent them
    QSet<QWebSocket *> m_audioViewers;
    QProcess *m_cava = nullptr;
    QByteArray m_cavaBuf;
    QTimer m_audioTimer;
    double m_bass = 0, m_bassRaw = 0, m_lastFrameAt = 0, m_lastTick = 0;
};
