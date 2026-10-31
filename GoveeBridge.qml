import QtQuick

// Add-on management UI, NOT the lamp's LED geometry or canvas placement.
// LED geometry comes from device.addChannel() + component/layout configuration.
Item {
    anchors.fill: parent
    Column {
        spacing: 12
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.margins: 18
        Text { text: "GoveeBridge"; color: "white"; font.pixelSize: 22; font.bold: true }
        Text {
            text: "Local .NET bridge at 127.0.0.1:41005."
            color: "#cccccc"
            font.pixelSize: 14
        }
        Text {
            text: "Configure the physical lamp IP in the service's private appsettings.local.json."
            color: "#cccccc"
            font.pixelSize: 13
            wrapMode: Text.WordWrap
            width: parent.width
        }
        Text {
            text: "Use the GoveeBridge.Cli app to query status, release, or resume PC control."
            color: "#cccccc"
            font.pixelSize: 13
            wrapMode: Text.WordWrap
            width: parent.width
        }
    }
}
