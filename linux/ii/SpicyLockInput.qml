// Lock-screen input relay for spicy-wallpaper.
//
// While the session is locked only the locker gets input, so the lyrics wallpaper
// underneath (visible through the transparent lock surface) can't be clicked. This sits
// in the locker's surface and puts invisible touch targets exactly over the page's
// clickable parts — lyric lines, cover, progress bar, prev/play/next — which spicy-wallpaper
// reports over ws://127.0.0.1:9012/input. A tap is sent back and replayed in the page.
// Everywhere else, clicks reach the lock screen as usual.
//
// Loaded from illogical-impulse's LockScreen.qml (see ../ii-lock-hint.patch):
//   Loader { anchors.fill: parent; source: ".../SpicyLockInput.qml"; onLoaded: item.screenName = ... }
import QtQuick
import QtWebSockets

Item {
    id: root

    property string screenName: ""
    property int port: 9012
    property var rects: []

    WebSocket {
        id: socket
        url: "ws://127.0.0.1:" + root.port + "/input"
        active: true
        onTextMessageReceived: message => {
            let m;
            try { m = JSON.parse(message); } catch (e) { return; }
            if (m.type !== "hitboxes") return;
            // The lock surface may not know its output name; adopt the page's.
            if (root.screenName === "") root.screenName = m.screen || "";
            if (m.screen === root.screenName) {
                root.rects = m.rects || [];
                console.log("[spicy] lock relay: " + root.rects.length + " regions on " + root.screenName);
            }
        }
        onStatusChanged: {
            if (socket.status === WebSocket.Error || socket.status === WebSocket.Closed) {
                root.rects = [];
                retry.start();
            }
        }
    }

    // spicy-wallpaper may start after the lock (or restart): keep trying quietly.
    Timer {
        id: retry
        interval: 3000
        onTriggered: {
            socket.active = false;
            socket.active = true;
        }
    }

    Repeater {
        model: root.rects
        delegate: MouseArea {
            required property var modelData
            x: modelData.x
            y: modelData.y
            width: modelData.w
            height: modelData.h
            cursorShape: Qt.PointingHandCursor
            onClicked: mouse => {
                console.log("[spicy] lock relay: tap " + modelData.kind + " at " + Math.round(x + mouse.x) + "," + Math.round(y + mouse.y));
                socket.sendTextMessage(JSON.stringify({
                    type: "tap",
                    screen: root.screenName,
                    x: x + mouse.x,
                    y: y + mouse.y
                }));
            }
        }
    }
}
