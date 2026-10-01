#pragma once

#include "Common.h"

#include <QObject>
#include <QTimer>
#include <optional>

class BridgeServer;
struct SpState;

/// Polls MPRIS (the D-Bus media-player interface every Linux player speaks — Spotify,
/// browsers, mpv, …) for the current track and timeline, feeding PositionEngine and
/// emitting trackChanged when the playing song changes. While Spotify is the active
/// player and the bridge is pushing its exact state, that wins over MPRIS.
/// Stands in for the Windows app's SMTC watcher.
class MprisWatcher : public QObject
{
    Q_OBJECT
public:
    explicit MprisWatcher(BridgeServer *bridge, QObject *parent = nullptr);
    void start();

    /// Playback control on the current player: "playpause" | "play" | "pause" | "next" |
    /// "prev" | "seek" (positionMs = player position to jump to).
    void control(const QString &action, double positionMs = 0);

signals:
    void trackChanged(const std::optional<TrackInfo> &info);
    /// Cover for the current track: a file:// or https:// URL, or empty when none.
    void artworkChanged(const QString &url);

private:
    void poll();
    void onBridgeState();
    bool emitIfChanged(const std::optional<TrackInfo> &info, bool immediate = false);
    void clearSession();
    QString pickPlayer(const QStringList &players);
    static TrackInfo fromBridge(const SpState &sp);

    BridgeServer *m_bridge;
    QTimer m_timer;
    QString m_lastKey, m_pendingKey;
    bool m_hasLast = false, m_hasPending = false;
    double m_pendingSince = 0;
    QString m_artworkKey, m_artworkUrl;
    QString m_lastActive;      // last player seen switching to Playing ("current" session)
    QHash<QString, bool> m_wasPlaying;
    bool m_spotifyIsCurrent = false;
    bool m_lastPlaying = false;
    QString m_player;          // MPRIS service currently shown
    QString m_trackPath;       // its mpris:trackid object path (needed by SetPosition)
    double m_seekGuardUntil = 0; // ignore position reports until the player has applied a seek
};
