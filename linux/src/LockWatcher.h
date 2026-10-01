#pragma once

#include <QDBusMessage>
#include <QObject>
#include <QTimer>

/// Tracks whether the session is locked, through logind's standard `LockedHint`
/// (org.freedesktop.login1.Session). Hyprland has no lock/unlock IPC event and the lock
/// surface belongs to the locker, so this relies on the locker setting the hint — see
/// linux/ii-lock-hint.patch for the illogical-impulse hook.
class LockWatcher : public QObject
{
    Q_OBJECT
public:
    explicit LockWatcher(QObject *parent = nullptr);
    void start();
    bool locked() const { return m_locked; }

signals:
    void lockedChanged(bool locked);

private slots:
    void onPropertiesChanged(const QString &iface, const QVariantMap &changed, const QStringList &invalidated);

private:
    QString findSessionPath();
    void refresh();
    void setLocked(bool locked);

    QString m_path;
    bool m_locked = false;
    QTimer m_poll; // belt and braces in case a signal is missed
};
