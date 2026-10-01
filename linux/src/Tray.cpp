#include "Tray.h"
#include "Common.h"
#include "Config.h"
#include "LyricsService.h"
#include "WallpaperFeed.h"
#include "WallpaperWindows.h"

#include <QActionGroup>
#include <QApplication>
#include <QDesktopServices>
#include <QFileDialog>
#include <QFileInfo>
#include <QFont>
#include <QIcon>
#include <QInputDialog>
#include <QMenu>
#include <QPainter>
#include <QPixmap>
#include <QSystemTrayIcon>
#include <QTimer>
#include <QUrl>

namespace {
QIcon makeIcon()
{
    QIcon icon;
    for (int size : {16, 22, 24, 32, 48, 64}) {
        QPixmap pm(size, size);
        pm.fill(Qt::transparent);
        QPainter p(&pm);
        p.setRenderHint(QPainter::TextAntialiasing);
        QFont f;
        f.setPixelSize(int(size * 0.85));
        f.setBold(true);
        p.setFont(f);
        p.setPen(Qt::white);
        p.drawText(pm.rect(), Qt::AlignCenter, QStringLiteral("♪"));
        icon.addPixmap(pm);
    }
    return icon;
}

QString title(const QString &c) { return c.isEmpty() ? c : c.left(1).toUpper() + c.mid(1); }

/// A submenu of mutually exclusive values with the current one checked.
template<typename T>
void choice(QMenu *parent, const QString &text, const QList<QPair<T, QString>> &options, const T &current,
            std::function<void(T)> set)
{
    QMenu *sub = parent->addMenu(text);
    auto *group = new QActionGroup(sub);
    group->setExclusive(true);
    for (const auto &[value, label] : options) {
        QAction *a = sub->addAction(label);
        a->setCheckable(true);
        a->setChecked(value == current);
        group->addAction(a);
        const T v = value;
        QObject::connect(a, &QAction::triggered, sub, [set, v] { set(v); });
    }
}
}

Tray::Tray(Config *cfg, WallpaperFeed *feed, WallpaperWindows *windows, QObject *parent)
    : QObject(parent), m_cfg(cfg), m_feed(feed), m_windows(windows)
{
    if (!QSystemTrayIcon::isSystemTrayAvailable())
        Log::write(QStringLiteral("tray: no StatusNotifier host yet; the icon appears once the bar's tray is up"));
    m_menu = new QMenu();
    m_icon = new QSystemTrayIcon(makeIcon(), this);
    m_icon->setToolTip(QStringLiteral("Spicy Wallpaper"));
    m_icon->setContextMenu(m_menu);
    rebuild();
    m_icon->show();
}

Tray::~Tray() { delete m_menu; }

void Tray::changed()
{
    m_cfg->save();
    m_feed->pushSettings();
    // Rebuild after the triggering action has finished delivering.
    QTimer::singleShot(0, this, &Tray::rebuild);
}

void Tray::addSurfaceMenu(QMenu *parent, const QString &label, WallpaperSettings &s)
{
    QMenu *root = parent->addMenu(label);
    auto set = [this](auto &field) {
        return [this, &field](std::remove_reference_t<decltype(field)> v) { field = v; changed(); };
    };

    choice<QString>(root, QStringLiteral("Background"),
                    {{"wallpapers", QStringLiteral("My wallpapers (new one each song)")}, {"dynamic", QStringLiteral("Album gradient")}},
                    s.background, set(s.background));
    QList<QPair<QString, QString>> collections{{"all", QStringLiteral("All")}};
    for (const QString &c : m_feed->categories()) collections.append({c, title(c)});
    choice<QString>(root, QStringLiteral("Collection"), collections, s.collection, set(s.collection));
    choice<QString>(root, QStringLiteral("Layout"),
                    {{"split", QStringLiteral("Cover + lyrics")}, {"lyrics", QStringLiteral("Lyrics only")},
                     {"lock", QStringLiteral("Centred (lock screen)")}},
                    s.layout, set(s.layout));
    root->addSeparator();

    QList<QPair<int, QString>> sizes, dims, blurs;
    for (int p : {80, 90, 100, 115, 130, 150}) sizes.append({p, QStringLiteral("%1%").arg(p)});
    for (int p : {0, 20, 35, 50, 65}) dims.append({p, p == 0 ? QStringLiteral("Off") : QStringLiteral("%1%").arg(p)});
    for (int px : {0, 6, 12, 24}) blurs.append({px, px == 0 ? QStringLiteral("Off") : QStringLiteral("%1 px").arg(px)});
    choice<int>(root, QStringLiteral("Lyrics size"), sizes, s.lyricsSize, set(s.lyricsSize));
    choice<int>(root, QStringLiteral("Darken background"), dims, s.dim, set(s.dim));
    choice<int>(root, QStringLiteral("Wallpaper blur"), blurs, s.wallpaperBlur, set(s.wallpaperBlur));
    root->addSeparator();

    auto toggle = [&](const QString &text, bool &field) {
        QAction *a = root->addAction(text);
        a->setCheckable(true);
        a->setChecked(field);
        connect(a, &QAction::triggered, this, [this, &field] { field = !field; changed(); });
    };
    toggle(QStringLiteral("Slow zoom drift"), s.kenBurns);
    toggle(QStringLiteral("Gradient reacts to bass"), s.audioReactive);
    toggle(QStringLiteral("Blur distant lines"), s.lineBlur);
    toggle(QStringLiteral("Spicy Lyrics font"), s.spicyFont);
    toggle(QStringLiteral("Clock when nothing plays"), s.clock);
}

void Tray::rebuild()
{
    m_menu->clear();

    m_menu->addAction(QStringLiteral("Next wallpaper"), this, [this] { m_feed->nextWallpaper(); });
    const QString folderName = m_cfg->wallpaperFolder.isEmpty()
        ? QStringLiteral("none") : QFileInfo(Paths::expandHome(m_cfg->wallpaperFolder)).fileName();
    m_menu->addAction(QStringLiteral("Folder… (%1)").arg(folderName), this, [this] {
        const QString dir = QFileDialog::getExistingDirectory(nullptr, QStringLiteral("Wallpaper folder"),
                                                              Paths::expandHome(m_cfg->wallpaperFolder));
        if (dir.isEmpty()) return;
        m_cfg->wallpaperFolder = dir;
        // Collections belong to the old folder.
        m_cfg->desktop.collection = m_cfg->lock.collection = QStringLiteral("all");
        changed();
    });
    m_menu->addSeparator();
    addSurfaceMenu(m_menu, QStringLiteral("Desktop"), m_cfg->desktop);
    addSurfaceMenu(m_menu, QStringLiteral("Lock screen"), m_cfg->lock);
    m_menu->addSeparator();

    // Read by the feed on every position push, so a change lands at once.
    const int off = m_cfg->globalOffsetMs;
    m_menu->addAction(QStringLiteral("Lyrics timing… (%1 ms)").arg(off == 0 ? QStringLiteral("0") : QString::asprintf("%+d", off)),
                      this, [this] {
        bool ok = false;
        const int ms = QInputDialog::getInt(nullptr, QStringLiteral("Lyrics timing"),
                                            QStringLiteral("Shift in ms. Positive = lyrics earlier (if they lag the song), negative = later."),
                                            m_cfg->globalOffsetMs, -5000, 5000, 50, &ok);
        if (!ok) return;
        m_cfg->globalOffsetMs = ms;
        changed();
    });
    QAction *interactive = m_menu->addAction(QStringLiteral("Clickable lyrics && controls"));
    interactive->setCheckable(true);
    interactive->setChecked(m_cfg->interactive);
    connect(interactive, &QAction::triggered, this, [this] { m_cfg->interactive = !m_cfg->interactive; changed(); });
    m_menu->addAction(QStringLiteral("Reload wallpaper"), this, [this] { m_windows->reload(); });
    m_menu->addAction(QStringLiteral("Open log"), this, [] { QDesktopServices::openUrl(QUrl::fromLocalFile(Log::path())); });
    m_menu->addAction(QStringLiteral("Open config"), this, [] { QDesktopServices::openUrl(QUrl::fromLocalFile(Config::path())); });
    m_menu->addAction(QStringLiteral("Clear lyrics cache"), this, [] { LyricsService::clearCache(); });
    m_menu->addSeparator();
    m_menu->addAction(QStringLiteral("Exit"), qApp, &QApplication::quit);
}
