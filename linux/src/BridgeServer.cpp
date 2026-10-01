#include "BridgeServer.h"
#include "Common.h"

#include <QJsonArray>
#include <QJsonDocument>
#include <QUrl>
#include <QUrlQuery>
#include <QWebSocket>
#include <QWebSocketServer>

bool SpState::isFresh() const { return nowMs() - receivedAt < 2500; }

double SpState::positionNowMs() const
{
    return playing ? positionMs + (nowMs() - receivedAt) : positionMs;
}

BridgeServer::BridgeServer(int port, QObject *parent) : QObject(parent), m_port(port) {}

BridgeServer::~BridgeServer()
{
    for (auto *s : m_servers) s->close();
}

void BridgeServer::start()
{
    // The extension dials ws://localhost:PORT; Chromium may resolve that to either
    // loopback family, so listen on both. Never on a routable address.
    for (const auto &addr : {QHostAddress(QHostAddress::LocalHost), QHostAddress(QHostAddress::LocalHostIPv6)}) {
        auto *server = new QWebSocketServer(QStringLiteral("spicy-wallpaper"), QWebSocketServer::NonSecureMode, this);
        if (!server->listen(addr, m_port)) {
            Log::write(QStringLiteral("bridge: listen on %1:%2 failed: %3")
                           .arg(addr.toString()).arg(m_port).arg(server->errorString()));
            delete server;
            continue;
        }
        connect(server, &QWebSocketServer::newConnection, this, [this, server] { onNewConnection(server); });
        m_servers.append(server);
        Log::write(QStringLiteral("bridge: listening on %1:%2").arg(addr.toString()).arg(m_port));
    }
}

/// Browser pages can open ws://localhost too. Only Spotify may take the extension slot
/// (any page could otherwise kick the real extension off and feed its own lyrics);
/// viewers must be local files (our WebEngine page) or localhost.
static bool originAllowed(const QString &origin, bool viewer)
{
    if (origin.isEmpty() || origin == QLatin1String("null") || origin.startsWith(QLatin1String("file:"), Qt::CaseInsensitive))
        return true;
    const QUrl u(origin);
    if (!u.isValid()) return false;
    const QString host = u.host();
    if (host == QLatin1String("localhost") || host == QLatin1String("127.0.0.1") || host == QLatin1String("::1"))
        return true;
    if (u.scheme() == QLatin1String("http") || u.scheme() == QLatin1String("https"))
        return !viewer && host.endsWith(QLatin1String("spotify.com"), Qt::CaseInsensitive);
    return !viewer;
}

void BridgeServer::onNewConnection(QWebSocketServer *server)
{
    while (QWebSocket *ws = server->nextPendingConnection()) {
        QString path = ws->requestUrl().path();
        while (path.endsWith(QLatin1Char('/'))) path.chop(1);
        const bool viewer = path.compare(QLatin1String("/wallpaper"), Qt::CaseInsensitive) == 0;
        const bool input = path.compare(QLatin1String("/input"), Qt::CaseInsensitive) == 0;
        const QString origin = ws->origin();
        if (!originAllowed(origin, viewer || input)) {
            Log::write(QStringLiteral("bridge: refused %1 connection from origin %2")
                           .arg(viewer ? u"viewer" : u"extension", origin));
            ws->close(QWebSocketProtocol::CloseCodePolicyViolated);
            ws->deleteLater();
            continue;
        }

        if (input) {
            m_inputs.insert(ws);
            Log::write(QStringLiteral("bridge: input relay connected (%1 total)").arg(m_inputs.size()));
            connect(ws, &QWebSocket::textMessageReceived, this, [this, ws](const QString &text) {
                const QJsonDocument doc = QJsonDocument::fromJson(text.toUtf8());
                if (doc.isObject()) emit inputMessage(ws, doc.object());
            });
            connect(ws, &QWebSocket::disconnected, this, [this, ws] {
                if (m_inputs.remove(ws)) Log::write(QStringLiteral("bridge: input relay disconnected"));
                ws->deleteLater();
            });
            emit inputConnected(ws);
        } else if (viewer) {
            m_viewers.insert(ws, QUrlQuery(ws->requestUrl()).queryItemValue(QStringLiteral("screen"), QUrl::FullyDecoded));
            Log::write(QStringLiteral("bridge: viewer connected (%1 total, origin %2)")
                           .arg(m_viewers.size()).arg(origin.isEmpty() ? QStringLiteral("-") : origin));
            connect(ws, &QWebSocket::textMessageReceived, this, [this, ws](const QString &text) {
                QJsonParseError err;
                const QJsonDocument doc = QJsonDocument::fromJson(text.toUtf8(), &err);
                if (doc.isObject()) emit viewerMessage(ws, doc.object());
                else Log::write(QStringLiteral("bridge: bad viewer message: ") + err.errorString());
            });
            connect(ws, &QWebSocket::disconnected, this, [this, ws] {
                if (m_viewers.remove(ws)) {
                    emit viewerDisconnected(ws);
                    Log::write(QStringLiteral("bridge: viewer disconnected (%1 left)").arg(m_viewers.size()));
                }
                ws->deleteLater();
            });
            emit viewerConnected(ws);
        } else {
            Log::write(QStringLiteral("bridge: spicetify extension connected (origin %1)")
                           .arg(origin.isEmpty() ? QStringLiteral("-") : origin));
            if (m_ext) {
                QWebSocket *old = m_ext;
                m_ext = nullptr;
                old->abort();
            }
            m_ext = ws;
            m_extOpen = true;
            connect(ws, &QWebSocket::textMessageReceived, this, &BridgeServer::handleExtensionMessage);
            connect(ws, &QWebSocket::disconnected, this, [this, ws] {
                if (m_ext == ws) {
                    m_ext = nullptr;
                    m_extOpen = false;
                    Log::write(QStringLiteral("bridge: extension disconnected"));
                }
                ws->deleteLater();
            });
            emit clientConnected();
        }
    }
}

bool BridgeServer::connected() const { return m_extOpen; }

std::optional<SpState> BridgeServer::spotifyState() const
{
    std::lock_guard g(m_stateLock);
    return m_state;
}

void BridgeServer::broadcast(const QJsonObject &msg)
{
    if (m_viewers.isEmpty()) return;
    const QString text = QString::fromUtf8(QJsonDocument(msg).toJson(QJsonDocument::Compact));
    for (auto it = m_viewers.cbegin(); it != m_viewers.cend(); ++it) it.key()->sendTextMessage(text);
}

void BridgeServer::sendToScreen(const QString &screen, const QJsonObject &msg)
{
    const QString text = QString::fromUtf8(QJsonDocument(msg).toJson(QJsonDocument::Compact));
    for (auto it = m_viewers.cbegin(); it != m_viewers.cend(); ++it)
        if (it.value() == screen) it.key()->sendTextMessage(text);
}

void BridgeServer::broadcastInputs(const QJsonObject &msg)
{
    if (m_inputs.isEmpty()) return;
    const QString text = QString::fromUtf8(QJsonDocument(msg).toJson(QJsonDocument::Compact));
    for (QWebSocket *ws : std::as_const(m_inputs)) ws->sendTextMessage(text);
}

void BridgeServer::sendInput(QWebSocket *input, const QJsonObject &msg)
{
    if (m_inputs.contains(input))
        input->sendTextMessage(QString::fromUtf8(QJsonDocument(msg).toJson(QJsonDocument::Compact)));
}

void BridgeServer::send(QWebSocket *viewer, const QJsonObject &msg)
{
    if (m_viewers.contains(viewer))
        viewer->sendTextMessage(QString::fromUtf8(QJsonDocument(msg).toJson(QJsonDocument::Compact)));
}

void BridgeServer::handleExtensionMessage(const QString &text)
{
    const QJsonObject root = QJsonDocument::fromJson(text.toUtf8()).object();
    const QString type = root.value("type").toString();
    if (type == QLatin1String("sp_state")) {
        SpState s;
        s.playing = root.value("playing").toBool(false);
        s.positionMs = root.value("positionMs").toDouble(0);
        s.durationMs = root.value("durationMs").toDouble(0);
        s.trackId = root.value("trackId").toString();
        s.title = root.value("title").toString();
        s.artist = root.value("artist").toString();
        s.album = root.value("album").toString();
        s.cover = root.value("cover").toString();
        s.receivedAt = nowMs();
        {
            std::lock_guard g(m_stateLock);
            m_state = s;
        }
        emit stateUpdated();
    } else if (type == QLatin1String("resp")) {
        const QString reqId = root.value("reqId").toString();
        std::shared_ptr<std::promise<QJsonObject>> p;
        {
            std::lock_guard g(m_pendingLock);
            p = m_pending.take(reqId);
        }
        if (p) p->set_value(root);
    } else if (type == QLatin1String("log")) {
        Log::write(QStringLiteral("ext: ") + root.value("msg").toString());
    } else if (type == QLatin1String("hello")) {
        Log::write(QStringLiteral("bridge: hello from extension (spicy-lyrics v%1)")
                       .arg(root.value("version").toString(QStringLiteral("?"))));
    }
}

std::optional<QJsonObject> BridgeServer::request(const QJsonObject &payload, const QString &reqId, int timeoutMs)
{
    if (!m_extOpen) return std::nullopt;
    auto p = std::make_shared<std::promise<QJsonObject>>();
    auto fut = p->get_future();
    {
        std::lock_guard g(m_pendingLock);
        m_pending.insert(reqId, p);
    }
    const QString text = QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact));
    QMetaObject::invokeMethod(this, [this, text] { if (m_ext) m_ext->sendTextMessage(text); }, Qt::QueuedConnection);

    std::optional<QJsonObject> result;
    if (fut.wait_for(std::chrono::milliseconds(timeoutMs)) == std::future_status::ready) result = fut.get();
    std::lock_guard g(m_pendingLock);
    m_pending.remove(reqId);
    return result;
}

std::optional<QList<TrackCandidate>> BridgeServer::search(const QString &query)
{
    const QString reqId = QStringLiteral("s%1").arg(++m_reqCounter);
    const auto r = request({{"type", "search"}, {"reqId", reqId}, {"query", query}}, reqId, 10000);
    if (!r) return std::nullopt;
    if (!r->value("ok").toBool(false)) {
        Log::write(QStringLiteral("bridge: search error: ") + r->value("error").toString(QStringLiteral("?")));
        return std::nullopt;
    }
    QList<TrackCandidate> list;
    for (const QJsonValue &v : r->value("results").toArray()) {
        const QJsonObject item = v.toObject();
        TrackCandidate c;
        c.id = item.value("id").toString();
        c.name = item.value("name").toString();
        c.durationMs = item.value("durationMs").toDouble(0);
        for (const QJsonValue &a : item.value("artists").toArray())
            if (a.isString()) c.artists.append(a.toString());
        if (!c.id.isEmpty()) list.append(c);
    }
    return list;
}

std::pair<int, QJsonObject> BridgeServer::lyrics(const QString &trackId)
{
    const QString reqId = QStringLiteral("l%1").arg(++m_reqCounter);
    // The extension may spend two 15s API attempts (401 -> refresh token -> retry).
    const auto r = request({{"type", "lyrics"}, {"reqId", reqId}, {"trackId", trackId}}, reqId, 35000);
    if (!r) return {0, {}};
    if (!r->value("ok").toBool(false)) {
        Log::write(QStringLiteral("bridge: lyrics error: ") + r->value("error").toString(QStringLiteral("?")));
        return {0, {}};
    }
    const int status = r->value("status").toInt(0);
    if (status == 200 && r->value("lyrics").isObject()) return {200, r->value("lyrics").toObject()};
    return {status, {}};
}
