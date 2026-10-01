#include "WallpaperFeed.h"
#include "BridgeServer.h"
#include "Config.h"

#include <QDir>
#include <QDirIterator>
#include <QFile>
#include <QFileInfo>
#include <QJsonArray>
#include <QMap>
#include <QStandardPaths>
#include <cmath>

namespace {
const QStringList kImageExt{"jpg", "jpeg", "png", "webp", "gif", "bmp", "avif"};

// cava reads PipeWire and prints one line of bar heights (0..1000) per frame. 16 bars
// from 30 Hz: the first two cover roughly the kick/bass range.
const char *kCavaConfig = R"([general]
bars = 16
framerate = 30
autosens = 1
lower_cutoff_freq = 30
higher_cutoff_freq = 16000

[input]
method = pipewire
source = auto

[output]
method = raw
raw_target = /dev/stdout
data_format = ascii
ascii_max_range = 1000
bar_delimiter = 59
frame_delimiter = 10
channels = mono

[smoothing]
noise_reduction = 20
)";
}

WallpaperFeed::WallpaperFeed(BridgeServer *bridge, Config *cfg, QObject *parent)
    : QObject(parent), m_bridge(bridge), m_cfg(cfg)
{
    m_lyrics = {{"type", "lyrics"}, {"state", "none"}, {"lyrics", QJsonValue::Null}};
    connect(m_bridge, &BridgeServer::viewerConnected, this, &WallpaperFeed::sendSnapshot);
    connect(m_bridge, &BridgeServer::viewerMessage, this, &WallpaperFeed::onViewerMessage);
    connect(m_bridge, &BridgeServer::viewerDisconnected, this, [this](QWebSocket *v) {
        if (m_audioViewers.remove(v)) updateAudio();
    });
    connect(m_bridge, &BridgeServer::inputMessage, this, &WallpaperFeed::onInputMessage);
    connect(m_bridge, &BridgeServer::inputConnected, this, [this](QWebSocket *input) {
        for (auto it = m_hitboxes.cbegin(); it != m_hitboxes.cend(); ++it)
            m_bridge->sendInput(input, {{"type", "hitboxes"}, {"screen", it.key()}, {"rects", it.value()}});
    });
    // MprisWatcher subscribed first, so PositionEngine already holds this push.
    connect(m_bridge, &BridgeServer::stateUpdated, this, [this] {
        if (PositionEngine::playing() != m_lastPlaying) pushPosition(); // pause/resume lands at once
    });
    connect(&m_posTimer, &QTimer::timeout, this, &WallpaperFeed::pushPosition);
    m_posTimer.start(250);
    connect(&m_audioTimer, &QTimer::timeout, this, [this] {
        const double now = nowMs();
        const double dt = now - m_lastTick;
        m_lastTick = now;
        // Fast attack, slow decay — same envelope the Windows audio engine used.
        const double decay = std::exp(-dt / 220.0);
        const double raw = now - m_lastFrameAt < 1500 ? m_bassRaw : 0;
        m_bass = raw > m_bass ? raw : m_bass * decay + raw * (1 - decay);
        const QJsonObject msg{{"type", "audio"}, {"bass", std::round(m_bass * 1000) / 1000}};
        for (QWebSocket *v : std::as_const(m_audioViewers)) m_bridge->send(v, msg);
    });
}

WallpaperFeed::~WallpaperFeed()
{
    if (m_cava) {
        m_cava->kill();
        m_cava->waitForFinished(500);
    }
}

void WallpaperFeed::onTrackChanged(const std::optional<TrackInfo> &info)
{
    if (!info) {
        m_track = {{"type", "track"}, {"track", QJsonValue::Null}};
    } else {
        m_track = {{"type", "track"}, {"track", QJsonObject{
            {"title", info->title}, {"artist", info->artist}, {"album", info->album},
            {"cover", info->coverUrl.isEmpty() ? QJsonValue(QJsonValue::Null) : QJsonValue(info->coverUrl)},
            {"durationMs", info->durationMs}, {"spotify", info->isSpotify},
        }}};
    }
    m_lyrics = {{"type", "lyrics"}, {"state", info ? "loading" : "none"}, {"lyrics", QJsonValue::Null}};
    m_art = {}; // the new track's MPRIS art follows via onArtwork
    m_bridge->broadcast(m_track);
    m_bridge->broadcast(m_lyrics);
    pushPosition();
}

void WallpaperFeed::onLyrics(const QJsonObject &lyrics)
{
    const bool has = !lyrics.isEmpty();
    m_lyrics = {{"type", "lyrics"}, {"state", has ? "ok" : "none"},
                {"lyrics", has ? QJsonValue(lyrics) : QJsonValue(QJsonValue::Null)}};
    m_bridge->broadcast(m_lyrics);
}

void WallpaperFeed::onArtwork(const QString &url)
{
    // The page is a file:// document, so it loads file:// and https:// covers directly.
    if (url.isEmpty()) { m_art = {}; return; }
    m_art = {{"type", "art"}, {"dataUrl", url}};
    m_bridge->broadcast(m_art);
}

void WallpaperFeed::setLocked(bool locked)
{
    if (locked == m_locked) return;
    m_locked = locked;
    Log::write(QStringLiteral("feed: surface -> ") + (locked ? QStringLiteral("lockscreen") : QStringLiteral("desktop")));
    m_bridge->broadcast(surfaceMessage());
}

QJsonObject WallpaperFeed::surfaceMessage() const
{
    return {{"type", "surface"}, {"surface", m_locked ? "lockscreen" : "desktop"}};
}

QJsonObject WallpaperFeed::positionMessage() const
{
    const auto now = PositionEngine::now();
    return {
        {"type", "pos"},
        {"has", now.has_value()},
        // The sync nudge from the tray ("Lyrics timing").
        {"ms", now.value_or(0) + m_cfg->globalOffsetMs},
        {"playing", PositionEngine::playing()},
        {"rate", PositionEngine::rate()},
    };
}

void WallpaperFeed::pushPosition()
{
    m_lastPlaying = PositionEngine::playing();
    if (m_bridge->hasViewers()) m_bridge->broadcast(positionMessage());
}

QJsonObject WallpaperFeed::settingsMessage() const
{
    return {
        {"type", "settings"},
        {"folder", Paths::expandHome(m_cfg->wallpaperFolder)},
        {"interactive", m_cfg->interactive},
        {"desktop", m_cfg->desktop.toMessage()},
        {"lockscreen", m_cfg->lock.toMessage()},
    };
}

void WallpaperFeed::pushSettings() { m_bridge->broadcast(settingsMessage()); }

void WallpaperFeed::nextWallpaper() { m_bridge->broadcast({{"type", "next-wallpaper"}}); }

void WallpaperFeed::sendSnapshot(QWebSocket *v)
{
    m_bridge->send(v, {{"type", "hello"}, {"app", "spicy-wallpaper"}, {"caps", QJsonObject{{"controls", true}}}});
    m_bridge->send(v, surfaceMessage());
    m_bridge->send(v, settingsMessage()); // early, so the page lays out right from the start
    if (!m_track.isEmpty()) m_bridge->send(v, m_track);
    m_bridge->send(v, m_lyrics);
    if (!m_art.isEmpty()) m_bridge->send(v, m_art);
    m_bridge->send(v, positionMessage());
}

void WallpaperFeed::onViewerMessage(QWebSocket *v, const QJsonObject &m)
{
    const QString type = m.value("type").toString();
    if (type == QLatin1String("list-wallpapers")) {
        const QString dir = m.value("dir").toString();
        const QStringList files = listImageFiles(dir);
        Log::write(QStringLiteral("feed: listed %1 wallpapers in %2").arg(files.size()).arg(dir));
        m_bridge->send(v, {
            {"type", "wallpapers"}, {"dir", dir}, {"files", QJsonArray::fromStringList(files)},
            {"error", files.isEmpty() ? QJsonValue(QStringLiteral("no images found")) : QJsonValue(QJsonValue::Null)},
        });
    } else if (type == QLatin1String("hitboxes")) {
        const QString screen = m_bridge->screenOf(v);
        const QJsonArray rects = m.value("rects").toArray();
        m_hitboxes.insert(screen, rects);
        QList<QRect> list;
        for (const QJsonValue &r : rects) {
            const QJsonObject o = r.toObject();
            list.append(QRect(o.value("x").toInt(), o.value("y").toInt(), o.value("w").toInt(), o.value("h").toInt()));
        }
        emit hitboxesChanged(screen, list);
        m_bridge->broadcastInputs({{"type", "hitboxes"}, {"screen", screen}, {"rects", rects}});
    } else if (type == QLatin1String("control")) {
        // The page speaks its clock's time, which carries our sync nudge; take it off.
        emit controlRequested(m.value("action").toString(), m.value("ms").toDouble(0) - m_cfg->globalOffsetMs);
    } else if (type == QLatin1String("audio")) {
        if (m.value("on").toBool(false)) m_audioViewers.insert(v);
        else m_audioViewers.remove(v);
        updateAudio();
    }
}

QStringList WallpaperFeed::listImageFiles(const QString &rawDir)
{
    const QString dir = Paths::expandHome(rawDir);
    if (dir.isEmpty() || !QDir(dir).exists()) return {};
    QStringList out;
    const int baseDepth = QDir::cleanPath(dir).count(QLatin1Char('/'));
    QDirIterator it(dir, QDir::Files | QDir::Readable, QDirIterator::Subdirectories | QDirIterator::FollowSymlinks);
    while (it.hasNext() && out.size() < 5000) {
        const QString f = it.next();
        if (f.count(QLatin1Char('/')) - baseDepth > 4) continue; // up to 3 subfolder levels
        if (kImageExt.contains(QFileInfo(f).suffix().toLower())) out.append(f);
    }
    return out;
}

QStringList WallpaperFeed::categories()
{
    const QString dir = Paths::expandHome(m_cfg->wallpaperFolder);
    if (dir == m_catDir && m_catAt.isValid() && m_catAt.secsTo(QDateTime::currentDateTimeUtc()) < 60) return m_catNames;
    QMap<QString, int> counts;
    const QString cleanDir = QDir::cleanPath(dir);
    for (const QString &f : listImageFiles(dir)) {
        const QFileInfo fi(f);
        const QString name = fi.completeBaseName();
        const int cut = name.indexOf(QLatin1Char('_'));
        if (cut > 0) counts[name.left(cut).toLower()]++;
        const QString parent = QDir::cleanPath(fi.absolutePath());
        if (parent != cleanDir) counts[QFileInfo(parent).fileName().toLower()]++;
    }
    QList<QPair<int, QString>> sorted;
    for (auto it = counts.begin(); it != counts.end(); ++it)
        if (it.value() >= 3) sorted.append({it.value(), it.key()});
    std::sort(sorted.begin(), sorted.end(), [](auto &a, auto &b) { return a.first > b.first; });
    QStringList names;
    for (const auto &p : sorted) {
        if (names.size() >= 12) break;
        if (!names.contains(p.second)) names.append(p.second);
    }
    m_catDir = dir;
    m_catAt = QDateTime::currentDateTimeUtc();
    m_catNames = names;
    return names;
}

void WallpaperFeed::onInputMessage(QWebSocket *, const QJsonObject &m)
{
    // Lock-screen relay: a tap on one of the page's regions, in that screen's logical px.
    if (m.value("type").toString() != QLatin1String("tap")) return;
    const QString screen = m.value("screen").toString();
    const QJsonObject tap{{"type", "tap"}, {"x", m.value("x").toDouble()}, {"y", m.value("y").toDouble()}};
    Log::write(QStringLiteral("feed: lock tap on %1 at %2,%3").arg(screen.isEmpty() ? QStringLiteral("?") : screen)
                   .arg(int(tap.value("x").toDouble())).arg(int(tap.value("y").toDouble())));
    // Unknown/mismatched output name: a single page is the common case, send it there.
    if (screen.isEmpty() || !m_hitboxes.contains(screen)) m_bridge->broadcast(tap);
    else m_bridge->sendToScreen(screen, tap);
}

// ---- audio (cava) — only runs while some page has asked for it ----

void WallpaperFeed::updateAudio()
{
    if (!m_audioViewers.isEmpty() && !m_cava) {
        if (QStandardPaths::findExecutable(QStringLiteral("cava")).isEmpty()) {
            Log::write(QStringLiteral("feed: cava not found; bass pulse disabled"));
            return;
        }
        const QString conf = Paths::cacheDir() + QStringLiteral("/cava.conf");
        QDir().mkpath(Paths::cacheDir());
        QFile f(conf);
        if (f.open(QIODevice::WriteOnly | QIODevice::Truncate)) f.write(kCavaConfig);
        f.close();
        m_cava = new QProcess(this);
        m_cava->setProcessChannelMode(QProcess::SeparateChannels);
        m_cava->setStandardErrorFile(QProcess::nullDevice());
        connect(m_cava, &QProcess::readyReadStandardOutput, this, &WallpaperFeed::readCava);
        connect(m_cava, &QProcess::finished, this, [this](int code) {
            Log::write(QStringLiteral("feed: cava exited (%1)").arg(code));
            m_cava->deleteLater();
            m_cava = nullptr;
            m_audioTimer.stop();
            // Restart if pages still want audio (PipeWire restarted, device change).
            if (!m_audioViewers.isEmpty()) QTimer::singleShot(2000, this, &WallpaperFeed::updateAudio);
        });
        m_cava->start(QStringLiteral("cava"), {QStringLiteral("-p"), conf});
        m_lastTick = nowMs();
        m_audioTimer.start(33);
        Log::write(QStringLiteral("feed: audio level stream on (cava)"));
    } else if (m_audioViewers.isEmpty() && m_cava) {
        m_audioTimer.stop();
        QProcess *p = m_cava;
        m_cava = nullptr;
        p->disconnect(this);
        p->kill();
        p->waitForFinished(500);
        p->deleteLater();
        m_bass = m_bassRaw = 0;
        Log::write(QStringLiteral("feed: audio level stream off"));
    }
}

void WallpaperFeed::readCava()
{
    if (!m_cava) return;
    m_cavaBuf += m_cava->readAllStandardOutput();
    int nl;
    QByteArray last;
    while ((nl = m_cavaBuf.indexOf('\n')) >= 0) {
        last = m_cavaBuf.left(nl);
        m_cavaBuf.remove(0, nl + 1);
    }
    if (last.isEmpty()) return;
    const QList<QByteArray> bars = last.split(';');
    if (bars.size() < 2) return;
    // cava's autosens already normalises loudness; scale down a little so the pulse
    // sits in the range the page expects from Wallpaper Engine's analyser.
    m_bassRaw = std::clamp((bars[0].toInt() + bars[1].toInt()) / 2000.0 * 0.85, 0.0, 1.0);
    m_lastFrameAt = nowMs();
}
