namespace GoveeBridge.Core;

public static class BridgeSettingsConstraints
{
   public const int MinimumFrameListenPort = 1024;
   public const int MaximumFrameListenPort = 65535;

   public const int MinimumLogicalLedCount = 1;
   public const int MaximumLogicalLedCount = 256;

   public const int MinimumSendIntervalMs = 100;
   public const int MaximumSendIntervalMs = 60000;

   public const int MinimumRemotePollIntervalMs = 500;
   public const int MaximumRemotePollIntervalMs = 60000;

   public const int MinimumLampTimeoutMs = 500;
   public const int MaximumLampTimeoutMs = 15000;
}
