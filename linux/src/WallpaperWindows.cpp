#include "WallpaperWindows.h"
#include "Common.h"
#include "Config.h"

#include <LayerShellQt/Window>
#include <QGuiApplication>
#include <QMetaObject>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QRegion>
#include <QScreen>
#include <QUrlQuery>

namespace {
// Named, on-disk profile: the page keeps its last settings and image listing in
// localStorage, so it looks right even before the bridge answers.
const char *kQml = R"(
import QtQuick
import QtWebEngine

Item {
    id: root
    property url pageUrl
    property string screenName
    property string storagePath
    property string cachePath
    function reload() { view.reload() }

    WebEngineProfile {
        id: profile
        storageName: "spicy-wallpaper"
        offTheRecord: false
        persistentStoragePath: root.storagePath
        cachePath: root.cachePath
    }

    WebEngineView {
        id: view
        anchors.fill: parent
        profile: profile
        backgroundColor: "black"
        url: root.pageUrl
        settings.localContentCanAccessFileUrls: true    // folder images
        settings.localContentCanAccessRemoteUrls: true  // covers, font, bridge socket
        settings.showScrollBars: false
        settings.webGLEnabled: true
        settings.accelerated2dCanvasEnabled: true
        settings.playbackRequiresUserGesture: false
        onJavaScriptConsoleMessage: (level, message, lineNumber, sourceID) =>
            wallpaperHost.console(root.screenName, level, message, lineNumber, sourceID)
        onRenderProcessTerminated: (status, code) => reloadTimer.start()
        onContextMenuRequested: (request) => request.accepted = true
    }

    // A crashed renderer comes back on its own.
    Timer { id: reloadTimer; interval: 2000; onTriggered: view.reload() }
}
)";
}

WallpaperWindows::WallpaperWindows(Config *cfg, QObject *parent) : QObject(parent), m_cfg(cfg) {}

WallpaperWindows::~WallpaperWindows()
{
    for (const Surface &s : std::as_const(m_surfaces)) {
        delete s.item;
        delete s.window;
    }
    m_surfaces.clear();
}

void WallpaperWindows::console(const QString &screen, int level, const QString &message, int line, const QString &source)
{
    if (level == 0) return; // info
    // The Spicy Lyrics font is only served to Spotify; the page falls back quietly.
    if (message.contains(QLatin1String("fonts.spikerko.org"))) return;
    Log::write(QStringLiteral("page[%1]: %2 (%3:%4)").arg(screen, message, source.section(QLatin1Char('/'), -1)).arg(line));
}

void WallpaperWindows::start()
{
    const QString dir = m_cfg->resolveWallpaperDir();
    if (dir.isEmpty()) {
        Log::write(QStringLiteral("wallpaper: index.html not found (set WallpaperDir in %1)").arg(Config::path()));
        return;
    }
    m_url = QUrl::fromLocalFile(dir + QStringLiteral("/index.html"));
    QUrlQuery q;
    q.addQueryItem(QStringLiteral("port"), QString::number(m_cfg->port));
    m_url.setQuery(q);
    Log::write(QStringLiteral("wallpaper: page ") + m_url.toString());

    m_engine = new QQmlEngine(this);
    m_engine->rootContext()->setContextProperty(QStringLiteral("wallpaperHost"), this);

    for (QScreen *screen : QGuiApplication::screens()) addScreen(screen);
    connect(qGuiApp, &QGuiApplication::screenAdded, this, &WallpaperWindows::addScreen);
    connect(qGuiApp, &QGuiApplication::screenRemoved, this, &WallpaperWindows::removeScreen);
}

void WallpaperWindows::addScreen(QScreen *screen)
{
    if (m_surfaces.contains(screen) || !m_engine) return;

    auto *win = new QQuickWindow();
    win->setColor(Qt::black);
    win->setScreen(screen);
    win->setTitle(QStringLiteral("spicy-wallpaper"));
    // Give it the layer-shell role before the platform window exists.
    auto *layer = LayerShellQt::Window::get(win);
    layer->setLayer(m_cfg->layer == QLatin1String("background") ? LayerShellQt::Window::LayerBackground
                                                                : LayerShellQt::Window::LayerBottom);
    layer->setAnchors(LayerShellQt::Window::Anchors(LayerShellQt::Window::AnchorTop | LayerShellQt::Window::AnchorBottom |
                                                    LayerShellQt::Window::AnchorLeft | LayerShellQt::Window::AnchorRight));
    layer->setExclusiveZone(-1); // cover the bar's reserved area too
    layer->setKeyboardInteractivity(LayerShellQt::Window::KeyboardInteractivityNone);
    layer->setScope(QStringLiteral("spicy-wallpaper"));
    layer->setScreenConfiguration(LayerShellQt::Window::ScreenFromQWindow);

    QQmlComponent component(m_engine);
    component.setData(kQml, QUrl(QStringLiteral("qrc:/spicy-wallpaper/Wallpaper.qml")));
    QUrl url = m_url;
    QUrlQuery q(url);
    q.addQueryItem(QStringLiteral("screen"), screen->name());
    url.setQuery(q);
    QObject *obj = component.createWithInitialProperties({
        {QStringLiteral("pageUrl"), url},
        {QStringLiteral("screenName"), screen->name()},
        {QStringLiteral("storagePath"), Paths::dataDir() + QStringLiteral("/web")},
        {QStringLiteral("cachePath"), Paths::cacheDir() + QStringLiteral("/web")},
    });
    auto *item = qobject_cast<QQuickItem *>(obj);
    if (!item) {
        Log::write(QStringLiteral("wallpaper: QML failed: ") + component.errorString());
        delete obj;
        delete win;
        return;
    }
    item->setParentItem(win->contentItem());
    auto fit = [win, item] { item->setSize(win->size()); };
    connect(win, &QWindow::widthChanged, item, fit);
    connect(win, &QWindow::heightChanged, item, fit);

    win->resize(screen->size());
    fit();
    win->show();
    // Tiny input region: clicks fall through to the desktop / shell underneath.
    win->setMask(QRegion(0, 0, 1, 1));
    m_surfaces.insert(screen, {win, item});
    Log::write(QStringLiteral("wallpaper: surface on %1 (%2x%3)").arg(screen->name()).arg(screen->size().width()).arg(screen->size().height()));
}

void WallpaperWindows::removeScreen(QScreen *screen)
{
    const Surface s = m_surfaces.take(screen);
    if (!s.window) return;
    Log::write(QStringLiteral("wallpaper: screen %1 removed").arg(screen->name()));
    s.item->deleteLater();
    s.window->deleteLater();
}

void WallpaperWindows::reload()
{
    for (const Surface &s : std::as_const(m_surfaces)) QMetaObject::invokeMethod(s.item, "reload");
}

void WallpaperWindows::setHitboxes(const QString &screenName, const QList<QRect> &rects)
{
    for (auto it = m_surfaces.cbegin(); it != m_surfaces.cend(); ++it) {
        if (it.key()->name() != screenName) continue;
        QRegion region;
        for (const QRect &r : rects) region += r.intersected(QRect(QPoint(0, 0), it.value().window->size()));
        it.value().window->setMask(region.isEmpty() ? QRegion(0, 0, 1, 1) : region);
    }
}
