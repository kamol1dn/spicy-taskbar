#include "Matching.h"

#include <QRegularExpression>
#include <QSet>
#include <algorithm>
#include <cmath>

namespace {
constexpr auto CI = QRegularExpression::CaseInsensitiveOption | QRegularExpression::UseUnicodePropertiesOption;
constexpr auto UNI = QRegularExpression::UseUnicodePropertiesOption;

const QRegularExpression &junkGroups()
{
    static const QRegularExpression re(
        QStringLiteral(R"([(\[][^)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|m\/?v|hd|hq|4k|remaster(ed)?|live|color coded|performance|explicit|clean|full)\b[^)\]]*[)\]])"),
        CI);
    return re;
}
const QRegularExpression &trailingPipe()
{
    static const QRegularExpression re(QStringLiteral(R"(\s*(\||//).*$)"), UNI);
    return re;
}
const QRegularExpression &featPart()
{
    static const QRegularExpression re(
        QStringLiteral(R"(\s*[(\[]\b(ft\.?|feat\.?|featuring|with)\b[^)\]]*[)\]]|\s*\b(ft\.?|feat\.?|featuring)\b.*$)"), CI);
    return re;
}
const QRegularExpression &artistTitle()
{
    static const QRegularExpression re(QStringLiteral(R"(^(.{1,60}?)\s*[-–—]\s+(.+)$)"), UNI);
    return re;
}
const QRegularExpression &spaces()
{
    static const QRegularExpression re(QStringLiteral(R"(\s+)"), UNI);
    return re;
}

QString trimChars(QString s, const QString &chars)
{
    int a = 0, b = s.size();
    while (a < b && chars.contains(s[a])) ++a;
    while (b > a && chars.contains(s[b - 1])) --b;
    return s.mid(a, b - a);
}

QSet<QString> tokens(const QString &normalized)
{
    const QStringList parts = normalized.split(QLatin1Char(' '), Qt::SkipEmptyParts);
    return QSet<QString>(parts.begin(), parts.end());
}

double tokenSim(const QString &a, const QString &b)
{
    const QString na = Matching::norm(a), nb = Matching::norm(b);
    const QSet<QString> ta = tokens(na), tb = tokens(nb);
    if (ta.isEmpty() || tb.isEmpty()) return 0;
    const double inter = QSet<QString>(ta).intersect(tb).size();
    double sim = inter / std::max(ta.size(), tb.size());

    // Squashed comparison rescues space-less names: "BillieEilish" (VEVO channel) vs
    // "Billie Eilish" tokenizes to zero overlap but is the same string without spaces.
    if (sim < 1) {
        QString sa = na, sb = nb;
        sa.remove(QLatin1Char(' '));
        sb.remove(QLatin1Char(' '));
        if (sa.size() > 2 && sb.size() > 2 && (sa == sb || sa.contains(sb) || sb.contains(sa)))
            sim = std::max(sim, 0.9);
    }
    return sim;
}
}

namespace Matching {

QString cleanTitle(const QString &title)
{
    QString t = title;
    t.replace(junkGroups(), QStringLiteral(" "));
    t.replace(trailingPipe(), QString());
    t.replace(spaces(), QStringLiteral(" "));
    return trimChars(t, QStringLiteral(" -–—"));
}

QString cleanArtist(const QString &artist)
{
    QString a = artist.trimmed();
    if (a.endsWith(QLatin1String(" - Topic"), Qt::CaseInsensitive)) a.chop(8); // YT auto-channels
    static const QRegularExpression vevo(QStringLiteral("VEVO$"), CI);
    a.replace(vevo, QString());
    return a.trimmed();
}

bool isIdentifiable(const QString &rawTitle, const QString &rawArtist)
{
    return !cleanArtist(rawArtist).isEmpty() || artistTitle().match(cleanTitle(rawTitle)).hasMatch();
}

QList<Cand> candidates(const QString &rawTitle, const QString &rawArtist)
{
    QList<Cand> list;
    const QString title = cleanTitle(rawTitle);
    const QString artist = cleanArtist(rawArtist);
    auto stripFeat = [](QString s) { return s.replace(featPart(), QStringLiteral(" ")).trimmed(); };

    // "Artist - Title" embedded in the video title beats the channel name.
    const auto m = artistTitle().match(title);
    if (m.hasMatch()) list.append({m.captured(1).trimmed(), stripFeat(m.captured(2))});
    if (!artist.isEmpty()) list.append({artist, stripFeat(title)});
    list.append({QString(), title});

    QList<Cand> out;
    for (const Cand &c : list)
        if (!c.second.isEmpty() && !out.contains(c)) out.append(c);
    return out;
}

QString norm(const QString &s)
{
    static const QRegularExpression punct(QStringLiteral(R"([^\w\s])"), UNI);
    QString n = s.toLower();
    n.replace(punct, QStringLiteral(" "));
    n.replace(spaces(), QStringLiteral(" "));
    return n.trimmed();
}

double score(const TrackCandidate &track, const Cand &cand, double durationMs)
{
    double titleSim = tokenSim(track.name, cand.second);
    // containment bonus: "Blinding Lights" inside "Blinding Lights (Remix)" etc.
    const QString nName = norm(track.name), nTitle = norm(cand.second);
    if (!nName.isEmpty() && !nTitle.isEmpty() && (nName.contains(nTitle) || nTitle.contains(nName)))
        titleSim = std::max(titleSim, 0.85);

    double artistSim = 0;
    if (!cand.first.isEmpty())
        for (const QString &a : track.artists) artistSim = std::max(artistSim, tokenSim(a, cand.first));

    // Duration is a boost when it agrees but only a mild penalty when it doesn't —
    // YouTube music videos carry intro/outro padding.
    double durBonus = 0;
    if (durationMs > 1000 && track.durationMs > 0) {
        const double diff = std::abs(track.durationMs - durationMs);
        durBonus = diff < 3000 ? 0.35 : diff < 8000 ? 0.2 : diff < 25000 ? 0 : -0.15;
    }
    return titleSim * 0.6 + artistSim * 0.4 + durBonus;
}

const TrackCandidate *pickBest(const QList<TrackCandidate> &results, const QList<Cand> &cands,
                               double durationMs, double *bestScore)
{
    const TrackCandidate *best = nullptr;
    double bs = 0;
    for (const TrackCandidate &t : results)
        for (const Cand &c : cands) {
            const double s = score(t, c, durationMs);
            if (s > bs) { bs = s; best = &t; }
        }
    if (bestScore) *bestScore = bs;
    return bs >= 0.55 ? best : nullptr;
}
}
