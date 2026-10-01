#include "LockWatcher.h"
#include "Common.h"

#include <QDBusArgument>
#include <QDBusConnection>
#include <QDBusObjectPath>
#include <QDBusVariant>
#include <unistd.h>

namespace {
const QString kLogin1 = QStringLiteral("org.freedesktop.login1");
const QString kManagerPath = QStringLiteral("/org/freedesktop/login1");
const QString kManager = QStringLiteral("org.freedesktop.login1.Manager");
const QString kSession = QStringLiteral("org.freedesktop.login1.Session");
const QString kProps = QStringLiteral("org.freedesktop.DBus.Properties");

QString objectPathReply(const QDBusMessage &r)
{
    if (r.type() != QDBusMessage::ReplyMessage || r.arguments().isEmpty()) return {};
    return r.arguments().first().value<QDBusObjectPath>().path();
}
}

LockWatcher::LockWatcher(QObject *parent) : QObject(parent)
{
    connect(&m_poll, &QTimer::timeout, this, &LockWatcher::refresh);
}

/// The graphical session this app belongs to. A systemd user service isn't inside the
/// session scope, so fall back from XDG_SESSION_ID / our PID to the user's display session.
QString LockWatcher::findSessionPath()
{
    auto bus = QDBusConnection::systemBus();
    const QString id = qEnvironmentVariable("XDG_SESSION_ID");
    if (!id.isEmpty()) {
        auto msg = QDBusMessage::createMethodCall(kLogin1, kManagerPath, kManager, QStringLiteral("GetSession"));
        msg << id;
        if (const QString p = objectPathReply(bus.call(msg, QDBus::Block, 1000)); !p.isEmpty()) return p;
    }
    {
        auto msg = QDBusMessage::createMethodCall(kLogin1, kManagerPath, kManager, QStringLiteral("GetSessionByPID"));
        msg << quint32(getpid());
        if (const QString p = objectPathReply(bus.call(msg, QDBus::Block, 1000)); !p.isEmpty()) return p;
    }
    // User's Display session: /org/freedesktop/login1/user/_UID has Display = (so).
    auto msg = QDBusMessage::createMethodCall(kLogin1, QStringLiteral("/org/freedesktop/login1/user/_%1").arg(getuid()),
                                              kProps, QStringLiteral("Get"));
    msg << QStringLiteral("org.freedesktop.login1.User") << QStringLiteral("Display");
    const QDBusMessage r = bus.call(msg, QDBus::Block, 1000);
    if (r.type() == QDBusMessage::ReplyMessage && !r.arguments().isEmpty()) {
        const QDBusArgument arg = r.arguments().first().value<QDBusVariant>().variant().value<QDBusArgument>();
        QString sid;
        QDBusObjectPath path;
        arg.beginStructure();
        arg >> sid >> path;
        arg.endStructure();
        if (!path.path().isEmpty() && path.path() != QLatin1String("/")) return path.path();
    }
    return {};
}

void LockWatcher::start()
{
    auto bus = QDBusConnection::systemBus();
    if (!bus.isConnected()) {
        Log::write(QStringLiteral("lock: no system bus; lock-screen layout disabled"));
        return;
    }
    m_path = findSessionPath();
    if (m_path.isEmpty()) {
        Log::write(QStringLiteral("lock: no logind session found; lock-screen layout disabled"));
        return;
    }
    Log::write(QStringLiteral("lock: watching LockedHint on ") + m_path);
    bus.connect(kLogin1, m_path, kProps, QStringLiteral("PropertiesChanged"), this,
                SLOT(onPropertiesChanged(QString, QVariantMap, QStringList)));
    refresh();
    m_poll.start(2000);
}

void LockWatcher::onPropertiesChanged(const QString &iface, const QVariantMap &changed, const QStringList &invalidated)
{
    if (iface != kSession) return;
    if (changed.contains(QStringLiteral("LockedHint"))) setLocked(changed.value(QStringLiteral("LockedHint")).toBool());
    else if (invalidated.contains(QStringLiteral("LockedHint"))) refresh();
}

void LockWatcher::refresh()
{
    if (m_path.isEmpty()) return;
    auto msg = QDBusMessage::createMethodCall(kLogin1, m_path, kProps, QStringLiteral("Get"));
    msg << kSession << QStringLiteral("LockedHint");
    const QDBusMessage r = QDBusConnection::systemBus().call(msg, QDBus::Block, 1000);
    if (r.type() == QDBusMessage::ReplyMessage && !r.arguments().isEmpty())
        setLocked(r.arguments().first().value<QDBusVariant>().variant().toBool());
}

void LockWatcher::setLocked(bool locked)
{
    if (locked == m_locked) return;
    m_locked = locked;
    Log::write(QStringLiteral("lock: ") + (locked ? QStringLiteral("locked") : QStringLiteral("unlocked")));
    emit lockedChanged(locked);
}
