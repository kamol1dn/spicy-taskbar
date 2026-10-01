#include "LyricsService.h"
#include "BridgeServer.h"
#include "Lyrics.h"

#include <QCryptographicHash>
#include <QDir>
#include <QEventLoop>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QNetworkAccessManager>
#include <QNetworkReply>
#include <QThread>
#include <QTimer>
#include <QUrlQuery>
#include <cmath>

namespace {
struct Cancelled {};

using Cancel = std::shared_ptr<std::atomic<bool>>;

void check(const Cancel &c)
{
    if (*c) throw Cancelled{};
}

void sleepFor(int ms, const Cancel &c)
{
    for (int waited = 0; waited < ms; waited += 50) {
        check(c);
        QThread::msleep(std::min(50, ms - waited));
    }
    check(c);
}

/// Blocking GET on the calling worker thread. Returns (status, body); status 0 on failure.
std::pair<int, QByteArray> httpGet(const QUrl &url, const Cancel &cancel)
{
    QNetworkAccessManager nam;
    QNetworkRequest req(url);
    req.setHeader(QNetworkRequest::UserAgentHeader, QStringLiteral("SpicyWallpaper/0.1 (personal use)"));
    req.setTransferTimeout(15000);
    QNetworkReply *reply = nam.get(req);
    QEventLoop loop;
    QObject::connect(reply, &QNetworkReply::finished, &loop, &QEventLoop::quit);
    QTimer poll;
    QObject::connect(&poll, &QTimer::timeout, reply, [&] { if (*cancel) reply->abort(); });
    poll.start(100);
    loop.exec();
    const int status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
    const QByteArray body = reply->readAll();
    if (reply->error() != QNetworkReply::NoError && status == 0 && !*cancel)
        Log::write(QStringLiteral("http: %1 failed: %2").arg(url.host(), reply->errorString()));
    reply->deleteLater();
    check(cancel);
    return {status, body};
}

QString cachePath(const QString &key)
{
    const QByteArray hash = QCryptographicHash::hash(key.toUtf8(), QCryptographicHash::Sha1).toHex().left(20);
    return LyricsService::cacheDir() + QLatin1Char('/') + QString::fromLatin1(hash) + QStringLiteral(".json");
}

std::optional<QJsonObject> loadCache(const QString &key)
{
    QFile f(cachePath(key));
    if (!f.open(QIODevice::ReadOnly)) return std::nullopt;
    const QJsonDocument doc = QJsonDocument::fromJson(f.readAll());
    if (!doc.isObject()) return std::nullopt;
    return doc.object();
}

void saveCache(const QString &key, const QJsonObject &lyrics)
{
    QDir().mkpath(LyricsService::cacheDir());
    QFile f(cachePath(key));
    if (f.open(QIODevice::WriteOnly | QIODevice::Truncate)) f.write(QJsonDocument(lyrics).toJson(QJsonDocument::Compact));
    else Log::write(QStringLiteral("cache: save failed: ") + f.errorString());
}

QString describe(const QJsonObject &cached)
{
    return LyricsNormalizer::hasLines(cached) ? cached.value("Source").toString() : QStringLiteral("negative");
}

QString extractSynced(const QJsonObject &o)
{
    const QString s = o.value("syncedLyrics").toString();
    return s.isEmpty() ? QString() : s;
}
}

LyricsService::LyricsService(BridgeServer *bridge, QObject *parent) : QObject(parent), m_bridge(bridge)
{
    QDir().mkpath(cacheDir());
}

LyricsService::~LyricsService()
{
    if (m_cancel) *m_cancel = true;
}

QString LyricsService::cacheDir() { return Paths::cacheDir() + QStringLiteral("/lyrics"); }

void LyricsService::clearCache()
{
    QDir(cacheDir()).removeRecursively();
    QDir().mkpath(cacheDir());
    Log::write(QStringLiteral("cache: cleared"));
}

void LyricsService::onTrackChanged(const std::optional<TrackInfo> &info)
{
    if (m_cancel) *m_cancel = true;
    const quint64 gen = ++m_generation;
    if (!info) {
        emit lyricsResolved({});
        return;
    }
    auto cancel = std::make_shared<std::atomic<bool>>(false);
    m_cancel = cancel;
    TrackInfo copy = *info;
    QThread *t = QThread::create([this, copy, cancel, gen] {
        QJsonObject result;
        try {
            result = resolve(copy, cancel);
        } catch (const Cancelled &) {
            return;
        } catch (const std::exception &e) {
            Log::write(QStringLiteral("resolve: error: ") + QString::fromUtf8(e.what()));
        }
        if (*cancel) return;
        QMetaObject::invokeMethod(this, [this, result, gen] {
            if (gen == m_generation) emit lyricsResolved(result);
        }, Qt::QueuedConnection);
    });
    connect(t, &QThread::finished, t, &QObject::deleteLater);
    t->start();
}

QJsonObject LyricsService::resolve(TrackInfo info, const Cancel &cancel)
{
    if (!info.isSpotify && !Matching::isIdentifiable(info.title, info.artist)) {
        Log::write(QStringLiteral("resolve: skipping \"%1\" (no artist to match on)").arg(info.title));
        return {};
    }

    // Metadata-only key: stable across the track id/duration trickling in.
    const QString cacheKey = Matching::norm(info.title) + QLatin1Char('|') + Matching::norm(info.artist);

    // Word-level (spicy) cache entries are final. lrclib/negative entries are served but
    // retried against SpicyLyrics whenever the bridge is up, so a fallback taken during a
    // bad moment can upgrade to word-level later.
    const auto cached = loadCache(cacheKey);
    if (cached && (cached->value("Source").toString() == QLatin1String("spicy") || !m_bridge->connected())) {
        Log::write(QStringLiteral("resolve: cache hit (%1)").arg(describe(*cached)));
        return LyricsNormalizer::hasLines(*cached) ? *cached : QJsonObject();
    }

    // Rapid skipping shouldn't fire a search per skipped track.
    if (info.spotifyTrackId.isEmpty()) sleepFor(600, cancel);

    // Spotify is the source but no track id yet — give the bridge a moment so we get
    // the exact id instead of searching.
    if (info.isSpotify && info.spotifyTrackId.isEmpty()) {
        for (int i = 0; i < 12 && info.spotifyTrackId.isEmpty(); ++i) {
            sleepFor(250, cancel);
            const auto sp = m_bridge->spotifyState();
            if (sp && sp->isFresh() && !sp->trackId.isEmpty() &&
                (sp->title.isEmpty() || sp->title.contains(info.title, Qt::CaseInsensitive) ||
                 info.title.contains(sp->title, Qt::CaseInsensitive))) {
                info.spotifyTrackId = sp->trackId;
                if (info.durationMs <= 0) info.durationMs = sp->durationMs;
            }
        }
    }

    QJsonObject lyrics;
    const bool bridgeWasUp = m_bridge->connected();

    // ---- 1) SpicyLyrics via the bridge ----
    if (bridgeWasUp) {
        const QString trackId = !info.spotifyTrackId.isEmpty() ? info.spotifyTrackId : findTrackId(info, cancel);
        if (!trackId.isEmpty()) {
            lyrics = fetchSpicy(trackId, cancel);
            if (!lyrics.isEmpty()) Log::write(QStringLiteral("resolve: spicy lyrics ok (%1)").arg(lyrics.value("Type").toString()));
        }
    }
    if (!lyrics.isEmpty()) {
        saveCache(cacheKey, lyrics);
        return lyrics;
    }

    // Spicy came up empty — an existing fallback cache entry still stands.
    if (cached) {
        Log::write(QStringLiteral("resolve: keeping cached %1 (no spicy upgrade)").arg(describe(*cached)));
        return LyricsNormalizer::hasLines(*cached) ? *cached : QJsonObject();
    }

    // ---- 2) LRCLIB fallback ----
    lyrics = fetchLrclib(info, cancel);
    if (!lyrics.isEmpty()) Log::write(QStringLiteral("resolve: lrclib ok"));

    // Cache positives always; negatives only when the bridge was up, so a temporarily
    // closed Spotify doesn't poison the cache.
    if (!lyrics.isEmpty()) saveCache(cacheKey, lyrics);
    else if (bridgeWasUp) saveCache(cacheKey, QJsonObject{{"Source", "none"}, {"Lines", QJsonArray()}});
    if (lyrics.isEmpty()) Log::write(QStringLiteral("resolve: no lyrics found"));
    return lyrics;
}

QString LyricsService::findTrackId(const TrackInfo &info, const Cancel &cancel)
{
    const QList<Cand> cands = Matching::candidates(info.title, info.artist);
    QStringList queries;
    for (int i = 0; i < std::min<qsizetype>(2, cands.size()); ++i) {
        const QString q = cands[i].first.isEmpty() ? cands[i].second : cands[i].second + QLatin1Char(' ') + cands[i].first;
        if (!queries.contains(q)) queries.append(q);
    }

    for (const QString &q : queries) {
        check(cancel);
        QList<TrackCandidate> results;
        bool memo = false;
        {
            std::lock_guard g(m_memoLock);
            if (m_searchMemo.contains(q)) { results = m_searchMemo.value(q); memo = true; }
        }
        if (!memo) {
            results = m_bridge->search(q).value_or(QList<TrackCandidate>());
            if (!results.isEmpty()) {
                std::lock_guard g(m_memoLock);
                m_searchMemo.insert(q, results);
            }
        }
        if (results.isEmpty()) {
            Log::write(QStringLiteral("resolve: search \"%1\" -> no usable results").arg(q));
            continue;
        }
        double score = 0;
        if (const TrackCandidate *best = Matching::pickBest(results, cands, info.durationMs, &score)) {
            Log::write(QStringLiteral("resolve: matched \"%1 - %2\" (score %3)")
                           .arg(best->artists.value(0), best->name).arg(score, 0, 'f', 2));
            return best->id;
        }
        const TrackCandidate &top = results.first();
        Log::write(QStringLiteral("resolve: search \"%1\" -> %2 results, best score %3 (top: %4 - %5, %6s vs %7s)")
                       .arg(q).arg(results.size()).arg(score, 0, 'f', 2).arg(top.artists.value(0), top.name)
                       .arg(int(top.durationMs / 1000)).arg(int(info.durationMs / 1000)));
    }
    Log::write(QStringLiteral("resolve: no confident spotify match"));
    return {};
}

QJsonObject LyricsService::fetchSpicy(const QString &trackId, const Cancel &cancel)
{
    // 503 = the server queued the request and is generating the lyrics. Poll on
    // spicy-lyrics' own schedule (2s, x1.5 per try, capped at 10s) for up to ~45s;
    // a track change cancels it early.
    for (int attempt = 0; attempt < 8; ++attempt) {
        check(cancel);
        const auto [status, json] = m_bridge->lyrics(trackId);
        if (status == 200) return LyricsNormalizer::fromSpicy(json);
        if (status == 503) {
            const int delay = int(std::min(10000.0, 2000 * std::pow(1.5, attempt)));
            Log::write(QStringLiteral("resolve: spicy queued (503), retrying in %1s...").arg(delay / 1000.0, 0, 'f', 1));
            sleepFor(delay, cancel);
            continue;
        }
        if (status != 0) Log::write(QStringLiteral("resolve: spicy status %1").arg(status));
        return {};
    }
    return {};
}

QJsonObject LyricsService::fetchLrclib(const TrackInfo &info, const Cancel &cancel)
{
    const double dur = info.durationMs;
    for (const Cand &c : Matching::candidates(info.title, info.artist)) {
        check(cancel);
        const QString &artist = c.first, &title = c.second;
        QString lrc;

        if (!artist.isEmpty() && dur > 1000) {
            QUrl url(QStringLiteral("https://lrclib.net/api/get"));
            QUrlQuery q;
            q.addQueryItem(QStringLiteral("artist_name"), artist);
            q.addQueryItem(QStringLiteral("track_name"), title);
            q.addQueryItem(QStringLiteral("duration"), QString::number(int(dur / 1000)));
            url.setQuery(q);
            const auto [status, body] = httpGet(url, cancel);
            if (status >= 200 && status < 300) lrc = extractSynced(QJsonDocument::fromJson(body).object());
        }

        if (lrc.isEmpty()) {
            QUrl url(QStringLiteral("https://lrclib.net/api/search"));
            QUrlQuery q;
            q.addQueryItem(QStringLiteral("track_name"), title);
            if (!artist.isEmpty()) q.addQueryItem(QStringLiteral("artist_name"), artist);
            url.setQuery(q);
            const auto [status, body] = httpGet(url, cancel);
            if (status >= 200 && status < 300) {
                const QJsonArray arr = QJsonDocument::fromJson(body).array();
                QJsonObject best;
                double bestDiff = INFINITY;
                bool found = false;
                for (const QJsonValue &v : arr) {
                    const QJsonObject e = v.toObject();
                    if (extractSynced(e).isEmpty()) continue;
                    const double d = e.value("duration").isDouble() && dur > 1000
                        ? std::abs(e.value("duration").toDouble() * 1000 - dur) : INFINITY;
                    if (!found || d < bestDiff) { best = e; bestDiff = d; found = true; }
                }
                if (found) {
                    const double bdur = best.value("duration").toDouble(0) * 1000;
                    if (dur < 1000 || bdur <= 0 || std::abs(bdur - dur) < 10000) lrc = extractSynced(best);
                }
            }
        }

        if (!lrc.isEmpty()) return LyricsNormalizer::fromLrc(lrc, dur);
    }
    return {};
}
