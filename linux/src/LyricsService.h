#pragma once

#include "Common.h"
#include "Matching.h"

#include <QHash>
#include <QJsonObject>
#include <QObject>
#include <atomic>
#include <memory>
#include <mutex>
#include <optional>

class BridgeServer;

/// Resolves a playing track to synced lyrics:
///   1. disk cache
///   2. SpicyLyrics API via the spicetify bridge (word-level when available)
///   3. LRCLIB (line-level, no auth) as fallback
/// Each resolve runs on its own worker thread; a newer track cancels the older one.
class LyricsService : public QObject
{
    Q_OBJECT
public:
    explicit LyricsService(BridgeServer *bridge, QObject *parent = nullptr);
    ~LyricsService() override;

    void onTrackChanged(const std::optional<TrackInfo> &info);
    static QString cacheDir();
    static void clearCache();

signals:
    /// Empty object = no lyrics.
    void lyricsResolved(const QJsonObject &lyrics);

private:
    using Cancel = std::shared_ptr<std::atomic<bool>>;
    QJsonObject resolve(TrackInfo info, const Cancel &cancel);
    QString findTrackId(const TrackInfo &info, const Cancel &cancel);
    QJsonObject fetchSpicy(const QString &trackId, const Cancel &cancel);
    QJsonObject fetchLrclib(const TrackInfo &info, const Cancel &cancel);

    BridgeServer *m_bridge;
    Cancel m_cancel;
    std::atomic<quint64> m_generation{0};

    std::mutex m_memoLock;
    QHash<QString, QList<TrackCandidate>> m_searchMemo;
};
