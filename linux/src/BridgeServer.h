#pragma once

#include "Matching.h"

#include <QHash>
#include <QJsonObject>
#include <QObject>
#include <QSet>
#include <future>
#include <memory>
#include <mutex>
#include <optional>

class QWebSocket;
class QWebSocketServer;

/// Spotify's own playback state, pushed by the spicetify extension ~4x a second.
struct SpState {
    bool playing = false;
    double positionMs = 0, durationMs = 0;
    QString trackId, title, artist, album, cover;
    double receivedAt = 0; // nowMs()

    bool isFresh() const;
    double positionNowMs() const; // extrapolated from when the push arrived
};

/// Local WebSocket server with three kinds of client:
///  * the spicetify extension (path "/") — request/response (search, lyrics) plus pushed
///    Spotify state; one at a time, a reconnect replaces the old socket.
///  * viewers (path "/wallpaper[?screen=NAME]") — any number of wallpaper pages that
///    receive broadcasts and ask for a few things (folder listing, audio level, controls).
///  * input relays (path "/input") — the lock-screen component, which forwards taps on
///    the page's clickable regions while the session is locked.
/// Lives on the main thread; search()/lyrics() block and must be called from a worker.
class BridgeServer : public QObject
{
    Q_OBJECT
public:
    explicit BridgeServer(int port, QObject *parent = nullptr);
    ~BridgeServer() override;
    void start();

    bool connected() const;
    std::optional<SpState> spotifyState() const;

    void broadcast(const QJsonObject &msg);
    void send(QWebSocket *viewer, const QJsonObject &msg);
    bool hasViewers() const { return !m_viewers.isEmpty(); }
    QString screenOf(QWebSocket *viewer) const { return m_viewers.value(viewer); }
    void sendToScreen(const QString &screen, const QJsonObject &msg);
    void broadcastInputs(const QJsonObject &msg);
    void sendInput(QWebSocket *input, const QJsonObject &msg);

    /// Blocking (worker thread only). Empty optional = bridge unavailable / error.
    std::optional<QList<TrackCandidate>> search(const QString &query);
    /// Blocking (worker thread only). (httpStatus, unpacked lyrics JSON); status 0 = unavailable.
    std::pair<int, QJsonObject> lyrics(const QString &trackId);

signals:
    void stateUpdated();             // a push landed; read spotifyState()
    void clientConnected();          // the extension (re)connected
    void viewerConnected(QWebSocket *viewer);
    void viewerMessage(QWebSocket *viewer, const QJsonObject &msg);
    void viewerDisconnected(QWebSocket *viewer);
    void inputConnected(QWebSocket *input);
    void inputMessage(QWebSocket *input, const QJsonObject &msg);

private:
    void onNewConnection(QWebSocketServer *server);
    void handleExtensionMessage(const QString &text);
    std::optional<QJsonObject> request(const QJsonObject &payload, const QString &reqId, int timeoutMs);

    int m_port;
    QList<QWebSocketServer *> m_servers;
    QWebSocket *m_ext = nullptr;
    std::atomic<bool> m_extOpen{false};
    QHash<QWebSocket *, QString> m_viewers; // -> screen name ("" when not given)
    QSet<QWebSocket *> m_inputs;

    mutable std::mutex m_stateLock;
    std::optional<SpState> m_state;

    std::mutex m_pendingLock;
    QHash<QString, std::shared_ptr<std::promise<QJsonObject>>> m_pending;
    std::atomic<int> m_reqCounter{0};
};
