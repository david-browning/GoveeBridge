namespace GoveeBridge.Protocol
{
    internal class GoveePayloadProperties
    {
        // Common JSON envelope
        public const string Message = "msg";
        public const string Command = "cmd";
        public const string Data = "data";

        // Commands and device status
        public const string Value = "value";
        public const string OnOff = "onOff";
        public const string Brightness = "brightness";
        public const string Color = "color";
        public const string ColorTemperatureKelvin = "colorTemInKelvin";

        // RGB color components
        public const string Red = "r";
        public const string Green = "g";
        public const string Blue = "b";

        // Device discovery
        public const string AccountTopic = "account_topic";
        public const string IpAddress = "ip";
        public const string DeviceId = "device";
        public const string Sku = "sku";
        public const string DeviceName = "deviceName";
        public const string BluetoothHardwareVersion = "bleVersionHard";
        public const string BluetoothSoftwareVersion = "bleVersionSoft";
        public const string WifiHardwareVersion = "wifiVersionHard";
        public const string WifiSoftwareVersion = "wifiVersionSoft";

        // Undocumented real-time protocol
        public const string Packet = "pt";
    }
}
