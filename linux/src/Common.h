#pragma once

#include <QJsonObject>
#include <QString>
#include <mutex>
#include <optional>

/// Appends to ~/.local/state/spicy-wallpaper/log.txt (rotated at 512 KB) and stderr.
namespace Log {
void write(const QString &msg);
QString path();
}

namespace Paths {
QString configDir();  // ~/.config/spicy-wallpaper
QString stateDir();   // ~/.local/state/spicy-wallpaper (log)
QString cacheDir();   // ~/.cache/spicy-wallpaper (lyrics cache)
QString dataDir();    // ~/.local/share/spicy-wallpaper (web storage)
QString expandHome(const QString &path);
}

/// Monotonic milliseconds since process start.
double nowMs();

/// The playing track, as the wallpaper and the lyrics resolver see it.
struct TrackInfo {
    QString title, artist, album;
    QString coverUrl;          // https cover from the Spotify bridge
    double durationMs = 0;
    bool isSpotify = false;
    QString spotifyTrackId;
    bool fromBridge = false;   // built from Spotify's own state push

    // MPRIS-sourced identity deliberately excludes the track id/duration: those can
    // trickle in later and must not look like a track change. Bridge-sourced info has
    // the id up front and keys on it, because during a mix transition Spotify swaps
    // tracks before its MPRIS metadata catches up.
    QString key() const
    {
        return fromBridge && !spotifyTrackId.isEmpty()
            ? QStringLiteral("sp:") + spotifyTrackId
            : title + QLatin1Char('|') + artist + QLatin1Char('|') + (isSpotify ? u"1" : u"0");
    }
};

/// Latest known playback position, interpolated between updates. Thread-safe.
class PositionEngine
{
public:
    static void set(double positionMs, bool playing, double rate = 1.0);
    static void clear();
    static std::optional<double> now();
    static bool playing();
    static double rate();

private:
    static std::mutex s_lock;
    static bool s_has, s_playing;
    static double s_base, s_at, s_rate;
};
