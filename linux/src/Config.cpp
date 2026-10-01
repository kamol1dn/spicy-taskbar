#include "Config.h"
#include "Common.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QJsonDocument>
#include <QStandardPaths>

WallpaperSettings WallpaperSettings::lockDefaults()
{
    WallpaperSettings s;
    s.layout = QStringLiteral("lock");
    s.clock = true; // our layer covers the shell's desktop clock widget, so draw one
    s.dim = 45;
    return s;
}

QJsonObject WallpaperSettings::toMessage() const
{
    return {
        {"background", background}, {"collection", collection}, {"layout", layout},
        {"lyricssize", lyricsSize}, {"dim", dim}, {"wallpaperblur", wallpaperBlur},
        {"kenburns", kenBurns}, {"audioreactive", audioReactive}, {"lineblur", lineBlur},
        {"spicyfont", spicyFont}, {"clock", clock},
    };
}

QJsonObject WallpaperSettings::toJson() const
{
    return {
        {"Background", background}, {"Collection", collection}, {"Layout", layout},
        {"LyricsSize", lyricsSize}, {"Dim", dim}, {"WallpaperBlur", wallpaperBlur},
        {"KenBurns", kenBurns}, {"AudioReactive", audioReactive}, {"LineBlur", lineBlur},
        {"SpicyFont", spicyFont}, {"Clock", clock},
    };
}

WallpaperSettings WallpaperSettings::fromJson(const QJsonObject &o, const WallpaperSettings &d)
{
    WallpaperSettings s;
    s.background = o.value("Background").toString(d.background);
    s.collection = o.value("Collection").toString(d.collection);
    s.layout = o.value("Layout").toString(d.layout);
    s.lyricsSize = o.value("LyricsSize").toInt(d.lyricsSize);
    s.dim = o.value("Dim").toInt(d.dim);
    s.wallpaperBlur = o.value("WallpaperBlur").toInt(d.wallpaperBlur);
    s.kenBurns = o.value("KenBurns").toBool(d.kenBurns);
    s.audioReactive = o.value("AudioReactive").toBool(d.audioReactive);
    s.lineBlur = o.value("LineBlur").toBool(d.lineBlur);
    s.spicyFont = o.value("SpicyFont").toBool(d.spicyFont);
    s.clock = o.value("Clock").toBool(d.clock);
    return s;
}

QString Config::path() { return Paths::configDir() + QStringLiteral("/config.json"); }

static QString defaultImageFolder()
{
    const QString pics = QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
    for (const char *name : {"Wallpapers", "wallpapers", "Wallpaper", "wallpaper"}) {
        const QString dir = pics + QLatin1Char('/') + QLatin1String(name);
        if (QDir(dir).exists()) return dir;
    }
    return {};
}

Config Config::load()
{
    Config c;
    QFile f(path());
    if (!f.exists()) {
        c.wallpaperFolder = defaultImageFolder();
        c.save();
        return c;
    }
    if (!f.open(QIODevice::ReadOnly)) return c;
    const QJsonObject o = QJsonDocument::fromJson(f.readAll()).object();
    c.port = o.value("Port").toInt(c.port);
    c.globalOffsetMs = o.value("GlobalOffsetMs").toInt(0);
    c.interactive = o.value("Interactive").toBool(true);
    c.layer = o.value("Layer").toString(c.layer);
    c.wallpaperDir = o.value("WallpaperDir").toString();
    c.wallpaperFolder = o.value("WallpaperFolder").toString(defaultImageFolder());
    c.desktop = WallpaperSettings::fromJson(o.value("WallpaperDesktop").toObject(), WallpaperSettings());
    c.lock = WallpaperSettings::fromJson(o.value("WallpaperLock").toObject(), WallpaperSettings::lockDefaults());
    return c;
}

void Config::save() const
{
    QDir().mkpath(Paths::configDir());
    const QJsonObject o{
        {"Port", port},
        {"GlobalOffsetMs", globalOffsetMs},
        {"Interactive", interactive},
        {"Layer", layer},
        {"WallpaperDir", wallpaperDir},
        {"WallpaperFolder", wallpaperFolder},
        {"WallpaperDesktop", desktop.toJson()},
        {"WallpaperLock", lock.toJson()},
    };
    QFile f(path());
    if (f.open(QIODevice::WriteOnly | QIODevice::Truncate))
        f.write(QJsonDocument(o).toJson(QJsonDocument::Indented));
    else
        Log::write(QStringLiteral("config: save failed: ") + f.errorString());
}

QString Config::resolveWallpaperDir() const
{
    const QString appDir = QCoreApplication::applicationDirPath();
    const QStringList candidates{
        Paths::expandHome(wallpaperDir),
        appDir + QStringLiteral("/../share/spicy-wallpaper/wallpaper"),
        QStringLiteral(SPICY_INSTALL_WALLPAPER_DIR),
        QStringLiteral(SPICY_SOURCE_WALLPAPER_DIR),
    };
    for (const QString &dir : candidates)
        if (!dir.isEmpty() && QFile::exists(dir + QStringLiteral("/index.html")))
            return QDir(dir).canonicalPath();
    return {};
}
