#include "Common.h"

#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <QFileInfo>
#include <QStandardPaths>
#include <cstdio>

namespace {
QString sub(QStandardPaths::StandardLocation loc, const char *fallback)
{
    QString base = QStandardPaths::writableLocation(loc);
    if (base.isEmpty()) base = QDir::homePath() + QLatin1String(fallback);
    return base + QStringLiteral("/spicy-wallpaper");
}
}

namespace Paths {
QString configDir() { return sub(QStandardPaths::GenericConfigLocation, "/.config"); }
QString cacheDir() { return sub(QStandardPaths::GenericCacheLocation, "/.cache"); }
QString dataDir() { return sub(QStandardPaths::GenericDataLocation, "/.local/share"); }
QString stateDir()
{
    const QString xdg = qEnvironmentVariable("XDG_STATE_HOME");
    return (xdg.isEmpty() ? QDir::homePath() + QStringLiteral("/.local/state") : xdg)
        + QStringLiteral("/spicy-wallpaper");
}
QString expandHome(const QString &path)
{
    QString p = path.trimmed();
    if (p == QLatin1String("~")) return QDir::homePath();
    if (p.startsWith(QLatin1String("~/"))) return QDir::homePath() + p.mid(1);
    return p;
}
}

namespace Log {
static std::mutex s_logLock;

QString path() { return Paths::stateDir() + QStringLiteral("/log.txt"); }

void write(const QString &msg)
{
    const QString line = QDateTime::currentDateTime().toString(QStringLiteral("HH:mm:ss ")) + msg;
    std::lock_guard g(s_logLock);
    std::fprintf(stderr, "[spicy-wallpaper] %s\n", qPrintable(msg));
    QDir().mkpath(Paths::stateDir());
    const QString p = path();
    if (QFileInfo(p).size() > 512 * 1024) QFile::remove(p);
    QFile f(p);
    if (f.open(QIODevice::Append | QIODevice::Text)) f.write((line + QLatin1Char('\n')).toUtf8());
}
}

static QElapsedTimer &monoClock()
{
    static QElapsedTimer t = [] { QElapsedTimer e; e.start(); return e; }();
    return t;
}

double nowMs() { return monoClock().nsecsElapsed() / 1e6; }

std::mutex PositionEngine::s_lock;
bool PositionEngine::s_has = false;
bool PositionEngine::s_playing = false;
double PositionEngine::s_base = 0;
double PositionEngine::s_at = 0;
double PositionEngine::s_rate = 1.0;

void PositionEngine::set(double positionMs, bool playing, double rate)
{
    std::lock_guard g(s_lock);
    s_has = true;
    s_base = positionMs;
    s_at = nowMs();
    s_playing = playing;
    s_rate = rate <= 0 ? 1.0 : rate;
}

void PositionEngine::clear()
{
    std::lock_guard g(s_lock);
    s_has = false;
}

std::optional<double> PositionEngine::now()
{
    std::lock_guard g(s_lock);
    if (!s_has) return std::nullopt;
    if (!s_playing) return s_base;
    return s_base + (nowMs() - s_at) * s_rate;
}

bool PositionEngine::playing()
{
    std::lock_guard g(s_lock);
    return s_has && s_playing;
}

double PositionEngine::rate()
{
    std::lock_guard g(s_lock);
    return s_rate;
}
