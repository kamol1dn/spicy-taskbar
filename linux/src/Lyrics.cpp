#include "Lyrics.h"

#include <QJsonArray>
#include <QRegularExpression>
#include <algorithm>
#include <vector>

namespace {
double sec(const QJsonObject &o, const char *key) { return o.value(QLatin1String(key)).toDouble(0) * 1000; }

/// Reads {Syllables: [...]} into (text, segs).
std::pair<QString, QJsonArray> readSyllables(const QJsonObject &holder)
{
    QJsonArray segs;
    QString text;
    for (const QJsonValue &v : holder.value("Syllables").toArray()) {
        const QJsonObject s = v.toObject();
        const bool part = s.value("IsPartOfWord").toBool(false);
        const QString t = s.value("Text").toString() + (part ? QString() : QStringLiteral(" "));
        segs.append(QJsonObject{
            {"Start", sec(s, "StartTime")}, {"End", sec(s, "EndTime")}, {"Text", t}, {"PartOfWord", part},
        });
        text += t;
    }
    while (text.endsWith(QLatin1Char(' '))) text.chop(1);
    return {text, segs};
}

QJsonArray sortedByStart(const QJsonArray &arr)
{
    std::vector<QJsonObject> v;
    for (const QJsonValue &x : arr) v.push_back(x.toObject());
    std::stable_sort(v.begin(), v.end(), [](const QJsonObject &a, const QJsonObject &b) {
        return a.value("Start").toDouble() < b.value("Start").toDouble();
    });
    QJsonArray out;
    for (const QJsonObject &o : v) out.append(o);
    return out;
}
}

namespace LyricsNormalizer {

QJsonObject fromSpicy(const QJsonObject &root)
{
    const QString type = root.value("Type").toString();
    if (type != QLatin1String("Syllable") && type != QLatin1String("Line")) return {}; // Static = unsynced
    const QJsonArray content = root.value("Content").toArray();
    if (content.isEmpty()) return {};

    QJsonArray lines, bgs;
    for (const QJsonValue &v : content) {
        const QJsonObject item = v.toObject();
        const bool opposite = item.value("OppositeAligned").toBool(false);
        if (type == QLatin1String("Line")) {
            const QString text = item.value("Text").toString();
            if (text.trimmed().isEmpty()) continue;
            lines.append(QJsonObject{
                {"Start", sec(item, "StartTime")}, {"End", sec(item, "EndTime")}, {"Text", text},
                {"Sylls", QJsonValue::Null}, {"Opposite", opposite},
            });
            continue;
        }
        // Syllable type: { Lead: {StartTime, EndTime, Syllables[]}, Background?: [...] }
        if (item.contains("Lead")) {
            const QJsonObject lead = item.value("Lead").toObject();
            auto [text, segs] = readSyllables(lead);
            if (!text.isEmpty())
                lines.append(QJsonObject{
                    {"Start", sec(lead, "StartTime")}, {"End", sec(lead, "EndTime")}, {"Text", text},
                    {"Sylls", segs}, {"Opposite", opposite},
                });
        }
        for (const QJsonValue &b : item.value("Background").toArray()) {
            const QJsonObject bg = b.toObject();
            auto [text, segs] = readSyllables(bg);
            if (text.isEmpty()) continue;
            bgs.append(QJsonObject{
                {"Start", sec(bg, "StartTime")}, {"End", sec(bg, "EndTime")}, {"Text", text},
                {"Sylls", segs}, {"Opposite", opposite},
            });
        }
    }

    lines = sortedByStart(lines);
    bgs = sortedByStart(bgs);
    if (lines.isEmpty()) return {};
    double start = root.value("StartTime").toDouble(0) * 1000;
    if (start <= 0) start = lines.first().toObject().value("Start").toDouble();
    return {{"Type", type}, {"StartTime", start}, {"Lines", lines}, {"Bg", bgs}, {"Source", "spicy"}};
}

QJsonObject fromLrc(const QString &lrc, double durationMs)
{
    static const QRegularExpression stamp(QStringLiteral(R"(\[(\d+):(\d+)(?:[.:](\d+))?\])"));
    std::vector<std::pair<double, QString>> entries;
    for (QString line : lrc.split(QLatin1Char('\n'))) {
        while (line.endsWith(QLatin1Char('\r'))) line.chop(1);
        QList<QRegularExpressionMatch> ms;
        for (auto it = stamp.globalMatch(line); it.hasNext();) ms.append(it.next());
        if (ms.isEmpty()) continue;
        const QString text = line.mid(ms.last().capturedEnd()).trimmed();
        for (const auto &m : ms) {
            double t = m.captured(1).toInt() * 60000.0 + m.captured(2).toInt() * 1000.0;
            if (m.hasCaptured(3) && !m.captured(3).isEmpty()) {
                const QString frac = m.captured(3);
                t += frac.size() == 2 ? frac.toInt() * 10 : frac.leftJustified(3, QLatin1Char('0')).left(3).toInt();
            }
            entries.emplace_back(t, text);
        }
    }
    std::stable_sort(entries.begin(), entries.end(), [](auto &a, auto &b) { return a.first < b.first; });
    if (entries.empty()) return {};

    QJsonArray lines;
    for (size_t i = 0; i < entries.size(); ++i) {
        if (entries[i].second.trimmed().isEmpty()) continue; // empty stamps mark gaps
        const double end = i + 1 < entries.size() ? entries[i + 1].first
                         : durationMs > entries[i].first ? durationMs : entries[i].first + 6000;
        lines.append(QJsonObject{
            {"Start", entries[i].first}, {"End", end}, {"Text", entries[i].second},
            {"Sylls", QJsonValue::Null}, {"Opposite", false},
        });
    }
    if (lines.isEmpty()) return {};
    return {{"Type", "Line"}, {"StartTime", lines.first().toObject().value("Start").toDouble()},
            {"Lines", lines}, {"Bg", QJsonArray()}, {"Source", "lrclib"}};
}
}
