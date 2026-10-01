#pragma once

#include <QList>
#include <QPair>
#include <QString>
#include <QStringList>

struct TrackCandidate {
    QString id, name;
    QStringList artists;
    double durationMs = 0;
};

/// (artist, title); artist may be empty.
using Cand = QPair<QString, QString>;

/// Cleans messy media metadata (YouTube video titles especially) into (artist, title)
/// candidates and scores Spotify search results against them.
namespace Matching {
QString cleanTitle(const QString &title);
QString cleanArtist(const QString &artist);

/// False for media with nothing to identify a song by: no artist and no "Artist - Title"
/// in the title. Browser tabs look like this (an Instagram reel reports just
/// "Instagram"), and a title-only search then matched an unrelated song.
bool isIdentifiable(const QString &rawTitle, const QString &rawArtist);

/// Best-first list of (artist, title) interpretations of the metadata.
QList<Cand> candidates(const QString &rawTitle, const QString &rawArtist);

QString norm(const QString &s);

/// Score a Spotify result against a candidate. ~[0..1.35].
double score(const TrackCandidate &track, const Cand &cand, double durationMs);

/// Best result scoring at least 0.55, or nullptr.
const TrackCandidate *pickBest(const QList<TrackCandidate> &results, const QList<Cand> &cands,
                               double durationMs, double *bestScore);
}
