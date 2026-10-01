#include "MprisWatcher.h"
#include "BridgeServer.h"

#include <QDBusArgument>
#include <QDBusConnection>
#include <QDBusConnectionInterface>
#include <QDBusMessage>
#include <QDBusObjectPath>
#include <QDBusPendingCall>
#include <QDBusVariant>
#include <cmath>

namespace {
const QString kPrefix = QStringLiteral("org.mpris.MediaPlayer2.");
const QString kPath = QStringLiteral("/org/mpris/MediaPlayer2");
const QString kPlayerIface = QStringLiteral("org.mpris.MediaPlayer2.Player");
const QString kProps = QStringLiteral("org.freedesktop.DBus.Properties");
constexpr int kCallTimeoutMs = 300; // a hung player must not stall the UI thread

QVariant unwrap(const QVariant &v)
{
    if (v.canConvert<QDBusVariant>()) return v.value<QDBusVariant>().variant();
    return v;
}

QVariantMap toMap(const QVariant &v)
{
    const QVariant u = unwrap(v);
    if (u.canConvert<QDBusArgument>()) return qdbus_cast<QVariantMap>(u.value<QDBusArgument>());
    return u.toMap();
}

QStringList toStringList(const QVariant &v)
{
    const QVariant u = unwrap(v);
    if (u.canConvert<QDBusArgument>()) return qdbus_cast<QStringList>(u.value<QDBusArgument>());
    if (u.typeId() == QMetaType::QString) return {u.toString()};
    return u.toStringList();
}

QString playbackStatus(const QString &service)
{
    auto msg = QDBusMessage::createMethodCall(service, kPath, kProps, QStringLiteral("Get"));
    msg << kPlayerIface << QStringLiteral("PlaybackStatus");
    const QDBusMessage r = QDBusConnection::sessionBus().call(msg, QDBus::Block, kCallTimeoutMs);
    if (r.type() != QDBusMessage::ReplyMessage || r.arguments().isEmpty()) return {};
    return unwrap(r.arguments().first()).toString();
}

QVariantMap playerProps(const QString &service)
{
    auto msg = QDBusMessage::createMethodCall(service, kPath, kProps, QStringLiteral("GetAll"));
    msg << kPlayerIface;
    const QDBusMessage r = QDBusConnection::sessionBus().call(msg, QDBus::Block, kCallTimeoutMs);
    if (r.type() != QDBusMessage::ReplyMessage || r.arguments().isEmpty()) return {};
    return toMap(r.arguments().first());
}

bool isSpotifyName(const QString &service) { return service.contains(QLatin1String("spotify"), Qt::CaseInsensitive); }

bool titlesRoughlyMatch(const QString &a, const QString &b)
{
    if (a.isEmpty() || b.isEmpty()) return true;
    return a.contains(b, Qt::CaseInsensitive) || b.contains(a, Qt::CaseInsensitive);
}
}

MprisWatcher::MprisWatcher(BridgeServer *bridge, QObject *parent) : QObject(parent), m_bridge(bridge)
{
    // Apply Spotify's pushes the instant they land: waiting for the next poll leaves a
    // stale baseline right after a mix transition seeks the incoming track.
    connect(m_bridge, &BridgeServer::stateUpdated, this, &MprisWatcher::onBridgeState);
    connect(&m_timer, &QTimer::timeout, this, &MprisWatcher::poll);
}

void MprisWatcher::start()
{
    if (!QDBusConnection::sessionBus().isConnected()) {
        Log::write(QStringLiteral("mpris: no session bus"));
        return;
    }
    m_timer.start(500);
    poll();
}

TrackInfo MprisWatcher::fromBridge(const SpState &sp)
{
    TrackInfo t;
    t.title = sp.title;
    t.artist = sp.artist;
    t.album = sp.album;
    t.coverUrl = sp.cover;
    t.durationMs = sp.durationMs;
    t.isSpotify = true;
    t.spotifyTrackId = sp.trackId;
    t.fromBridge = true;
    return t;
}

void MprisWatcher::onBridgeState()
{
    if (!m_spotifyIsCurrent) return;
    const auto sp = m_bridge->spotifyState();
    if (!sp) return;
    if (nowMs() >= m_seekGuardUntil) PositionEngine::set(sp->positionNowMs(), sp->playing);
    if (sp->trackId.isEmpty()) return;
    const TrackInfo info = fromBridge(*sp);
    // Spotify changing track is authoritative and never flickers: no debounce.
    if (!m_hasLast || info.key() != m_lastKey) emitIfChanged(info, true);
}

/// MPRIS has no "current session" like Windows does. Prefer whatever is actually
/// playing (Spotify first), then the player that most recently started playing, then
/// anything that has a track loaded.
QString MprisWatcher::pickPlayer(const QStringList &players)
{
    QString playing;
    for (const QString &p : players) {
        const bool isPlaying = playbackStatus(p) == QLatin1String("Playing");
        if (isPlaying && !m_wasPlaying.value(p)) m_lastActive = p;
        m_wasPlaying[p] = isPlaying;
        if (!isPlaying) continue;
        if (isSpotifyName(p)) return p;
        if (playing.isEmpty()) playing = p;
    }
    for (auto it = m_wasPlaying.begin(); it != m_wasPlaying.end();)
        it = players.contains(it.key()) ? std::next(it) : m_wasPlaying.erase(it);
    if (!playing.isEmpty()) return playing;
    if (players.contains(m_lastActive)) return m_lastActive;
    return players.isEmpty() ? QString() : players.first();
}

void MprisWatcher::clearSession()
{
    m_spotifyIsCurrent = false;
    PositionEngine::clear();
    if (emitIfChanged(std::nullopt)) {
        m_artworkKey.clear();
        m_artworkUrl.clear();
        emit artworkChanged(QString());
    }
}

void MprisWatcher::poll()
{
    QStringList players;
    const auto names = QDBusConnection::sessionBus().interface()->registeredServiceNames();
    for (const QString &n : names.value())
        if (n.startsWith(kPrefix) && !n.endsWith(QLatin1String(".playerctld"))) players.append(n);

    const QString player = pickPlayer(players);
    m_player = player;
    if (player.isEmpty()) { clearSession(); return; }

    const QVariantMap props = playerProps(player);
    const QVariantMap meta = toMap(props.value(QStringLiteral("Metadata")));
    const QString title = unwrap(meta.value(QStringLiteral("xesam:title"))).toString();
    if (title.trimmed().isEmpty()) { clearSession(); return; }

    const bool isSpotify = isSpotifyName(player);
    m_spotifyIsCurrent = isSpotify;

    TrackInfo info;
    info.title = title;
    info.artist = toStringList(meta.value(QStringLiteral("xesam:artist"))).join(QStringLiteral(", "));
    info.album = unwrap(meta.value(QStringLiteral("xesam:album"))).toString();
    info.isSpotify = isSpotify;
    info.durationMs = unwrap(meta.value(QStringLiteral("mpris:length"))).toLongLong() / 1000.0;
    // Spotify's MPRIS trackid is "/com/spotify/track/<id>": the exact id even before the
    // bridge has pushed anything. Not part of the key (see TrackInfo::key).
    const QVariant rawTrackId = unwrap(meta.value(QStringLiteral("mpris:trackid")));
    const QString mprisTrackId = rawTrackId.canConvert<QDBusObjectPath>() && rawTrackId.typeId() != QMetaType::QString
        ? rawTrackId.value<QDBusObjectPath>().path() : rawTrackId.toString();
    m_trackPath = mprisTrackId;
    if (isSpotify && mprisTrackId.contains(QLatin1String("/track/")))
        info.spotifyTrackId = mprisTrackId.section(QLatin1Char('/'), -1);
    const QString artUrl = unwrap(meta.value(QStringLiteral("mpris:artUrl"))).toString();

    const bool playing = unwrap(props.value(QStringLiteral("PlaybackStatus"))).toString() == QLatin1String("Playing");
    double rate = unwrap(props.value(QStringLiteral("Rate"))).toDouble();
    if (rate <= 0) rate = 1.0;

    const auto sp = m_bridge->spotifyState();
    // Chromium throttles the extension's push timer while Spotify is in the background,
    // so pushes can pause. While MPRIS still names the song the bridge last reported,
    // keep the bridge's identity (track id, full artist list): otherwise the key would
    // flip between "sp:<id>" and "Title|Artist", re-resolving the lyrics every time.
    const bool bridgeSameSong = isSpotify && sp && !sp->trackId.isEmpty() && titlesRoughlyMatch(sp->title, title);
    if (isSpotify && sp && sp->isFresh() && !sp->trackId.isEmpty()) {
        // Spotify's own clock, pushed from inside the app — always authoritative, even
        // when MPRIS still names the previous track during a mix transition.
        if (!titlesRoughlyMatch(sp->title, title))
            Log::write(QStringLiteral("mpris: bridge/MPRIS disagree (bridge \"%1\" vs mpris \"%2\") — trusting bridge")
                           .arg(sp->title, title));
        info = fromBridge(*sp);
        if (nowMs() >= m_seekGuardUntil) PositionEngine::set(sp->positionNowMs(), sp->playing);
    } else {
        auto posMsg = QDBusMessage::createMethodCall(player, kPath, kProps, QStringLiteral("Get"));
        posMsg << kPlayerIface << QStringLiteral("Position");
        const QDBusMessage r = QDBusConnection::sessionBus().call(posMsg, QDBus::Block, kCallTimeoutMs);
        const bool hasPos = r.type() == QDBusMessage::ReplyMessage && !r.arguments().isEmpty();
        const double posMs = hasPos ? unwrap(r.arguments().first()).toLongLong() / 1000.0 : 0;
        // MPRIS position is live, unlike SMTC's. Only rebase on a real jump (seek, track
        // change) or a play/pause flip, so the interpolation doesn't jitter every poll.
        const auto predicted = PositionEngine::now();
        if (nowMs() >= m_seekGuardUntil && (!predicted || playing != m_lastPlaying || std::abs(*predicted - posMs) > 400))
            PositionEngine::set(posMs, playing, rate);
        if (bridgeSameSong) info = fromBridge(*sp); // identity only; position stays MPRIS'
    }
    m_lastPlaying = playing;

    emitIfChanged(info);

    // Wait until MPRIS names the same track as the bridge before taking its art, or a
    // mix transition would show the previous cover.
    const bool artInSync = !isSpotify || !sp || !sp->isFresh() || titlesRoughlyMatch(sp->title, title);
    if (artInSync && m_hasLast && (m_lastKey != m_artworkKey || artUrl != m_artworkUrl)) {
        m_artworkKey = m_lastKey;
        m_artworkUrl = artUrl;
        emit artworkChanged(artUrl);
    }
}

bool MprisWatcher::emitIfChanged(const std::optional<TrackInfo> &info, bool immediate)
{
    const bool has = info.has_value();
    const QString key = has ? info->key() : QString();
    if (has == m_hasLast && key == m_lastKey) { m_hasPending = false; return false; }

    // Debounce: browsers flicker metadata while loading — require the new identity to
    // hold for 700 ms before treating it as a real track change.
    if (!immediate) {
        if (!m_hasPending || key != m_pendingKey) {
            m_hasPending = true;
            m_pendingKey = key;
            m_pendingSince = nowMs();
            return false;
        }
        if (nowMs() - m_pendingSince < 700) return false;
    }

    m_hasLast = has;
    m_lastKey = key;
    m_hasPending = false;
    Log::write(QStringLiteral("mpris: track -> ") +
               (has ? info->artist + QStringLiteral(" - ") + info->title + (info->isSpotify ? QStringLiteral(" [spotify]") : QString())
                    : QStringLiteral("(none)")));
    emit trackChanged(info);
    return true;
}

void MprisWatcher::control(const QString &action, double positionMs)
{
    if (m_player.isEmpty()) return;
    static const QHash<QString, QString> methods{
        {QStringLiteral("playpause"), QStringLiteral("PlayPause")}, {QStringLiteral("play"), QStringLiteral("Play")},
        {QStringLiteral("pause"), QStringLiteral("Pause")}, {QStringLiteral("next"), QStringLiteral("Next")},
        {QStringLiteral("prev"), QStringLiteral("Previous")},
    };
    QDBusMessage msg;
    if (action == QLatin1String("seek")) {
        if (m_trackPath.isEmpty() || !m_trackPath.startsWith(QLatin1Char('/'))) {
            Log::write(QStringLiteral("mpris: can't seek %1 (no track id)").arg(m_player));
            return;
        }
        const double ms = std::max(0.0, positionMs);
        msg = QDBusMessage::createMethodCall(m_player, kPath, kPlayerIface, QStringLiteral("SetPosition"));
        msg << QVariant::fromValue(QDBusObjectPath(m_trackPath)) << qlonglong(ms * 1000);
        // Show it at once; the next poll / bridge push confirms.
        PositionEngine::set(ms, PositionEngine::playing(), PositionEngine::rate());
        m_seekGuardUntil = nowMs() + 700;
    } else if (methods.contains(action)) {
        msg = QDBusMessage::createMethodCall(m_player, kPath, kPlayerIface, methods.value(action));
    } else {
        return;
    }
    Log::write(QStringLiteral("mpris: %1 -> %2").arg(action, m_player));
    QDBusConnection::sessionBus().asyncCall(msg, 1000);
    QTimer::singleShot(150, this, &MprisWatcher::poll);
}
