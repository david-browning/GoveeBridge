import QtQuick

// Add-on management UI; physical device configuration lives in the .NET Host.
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
            text: "Configure physical lamps under Bridge:Lamps in appsettings.local.json."
            color: "#cccccc"
            font.pixelSize: 13
            wrapMode: Text.WordWrap
            width: parent.width
        }
        Text {
            text: "Use PowerShell: Resume-GoveeHostControl, then Arm-GoveeHostStreaming. Frames do not auto-arm."
            color: "#cccccc"
            font.pixelSize: 13
            wrapMode: Text.WordWrap
            width: parent.width
        }
    }
}
