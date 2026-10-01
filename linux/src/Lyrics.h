#pragma once

#include <QJsonArray>
#include <QJsonObject>
#include <QString>

/// Lyrics travel as JSON in the shape the wallpaper renderer (wallpaper/js/lyrics.js)
/// reads — the same field names the Windows app serialized:
///   { Type: "Syllable"|"Line", StartTime, Source,
///     Lines: [{ Start, End, Text, Sylls: [{Start, End, Text, PartOfWord}] | null, Opposite }],
///     Bg:    [{ Start, End, Text, Sylls, Opposite }] }   (times in ms)
/// An empty object means "no lyrics".
namespace LyricsNormalizer {
/// SpicyLyrics API JSON (seconds) -> our model (ms). Empty for Static/unusable.
QJsonObject fromSpicy(const QJsonObject &root);

/// LRC text (from LRCLIB) -> line-synced lyrics. Empty when nothing parses.
QJsonObject fromLrc(const QString &lrc, double durationMs);

inline bool hasLines(const QJsonObject &l) { return !l.value("Lines").toArray().isEmpty(); }
}
