namespace GoveeBridge.Core.Ipc;

/// <summary>
/// Builds application-facing IPC requests without opening sockets or taking control.
/// </summary>
public static class BridgeIpcRequests
{
   /// <summary>
   /// Gets the selected lamp's cached Host state without polling the device.
   /// </summary>
   public static BridgeIpcCommandRequest Status(string lampId) =>
      new(BridgeIpcActions.GetHostStatus, lampId);

   /// <summary>
   /// Requests a new physical observation, when supported by the current mode.
   /// </summary>
   public static BridgeIpcCommandRequest LampStatus(string lampId) =>
      new(BridgeIpcActions.GetLampStatus, lampId);

   /// <summary>
   /// Claims PC Direct control without replaying frames received in Manual mode.
   /// </summary>
   public static BridgeIpcCommandRequest Resume(string lampId) =>
      new(BridgeIpcActions.Resume, lampId);

   /// <summary>
   /// Releases Host authority and attempts to disarm experimental streaming.
   /// </summary>
   public static BridgeIpcCommandRequest Release(string lampId) =>
      new(BridgeIpcActions.Release, lampId);

   /// <summary>
   /// Requests transition from PC Direct to PC Streaming.
   /// </summary>
   public static BridgeIpcCommandRequest Arm(string lampId) =>
      new(BridgeIpcActions.Arm, lampId);

   /// <summary>
   /// Requests transition back to PC Direct, including disarm retry when needed.
   /// </summary>
   public static BridgeIpcCommandRequest Disarm(string lampId) =>
      new(BridgeIpcActions.Disarm, lampId);

   /// <summary>
   /// Requests an explicit power change in a PC-controlled mode.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcPowerPayload> SetPower(
      bool isOn, string lampId) =>
      new(BridgeIpcActions.SetPower, new BridgeIpcPowerPayload { IsOn = isOn }, lampId);

   /// <summary>
   /// Requests brightness 0..100, with zero mapping to power off at Runtime.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcBrightnessPayload> SetBrightness(
      int brightnessPercent, string lampId)
   {
      ArgumentOutOfRangeException.ThrowIfNegative(brightnessPercent);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(brightnessPercent, 100);
      return new(BridgeIpcActions.SetBrightness,
         new BridgeIpcBrightnessPayload { BrightnessPercent = brightnessPercent }, lampId);
   }

   /// <summary>
   /// Requests a solid RGB color while in PC Direct mode.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcColorPayload> SetColor(
      RgbColor color, string lampId) =>
      new(BridgeIpcActions.SetSolidColor, new BridgeIpcColorPayload
      {
         Color = new BridgeIpcRgbColor(color.R, color.G, color.B)
      }, lampId);

   /// <summary>
   /// Requests white temperature while in PC Direct mode.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcColorTemperaturePayload> SetColorTemperature(
      int kelvin, string lampId)
   {
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(kelvin);
      return new(BridgeIpcActions.SetColorTemperature,
         new BridgeIpcColorTemperaturePayload { Kelvin = kelvin }, lampId);
   }

   /// <summary>
   /// Sends one complete frame to the selected lamp's latest-frame mailbox.
   /// </summary>
   public static BridgeIpcFrameRequest SendFrame(RgbFrame frame, string lampId)
   {
      ArgumentNullException.ThrowIfNull(frame);
      IReadOnlyList<RgbColor> colors = frame.Colors;
      if (colors is null ||
             colors.Count is < 1 or > BridgeSettingsConstraints.MaximumFrameColorCount)
      {
         throw new ArgumentException(
            $"A frame must contain 1 to {BridgeSettingsConstraints.MaximumFrameColorCount} colors.", nameof(frame));
      }

      int[] components = new int[colors.Count * 3];
      for (int index = 0; index < colors.Count; index++)
      {
         int offset = index * 3;
         RgbColor color = colors[index];
         components[offset] = color.R;
         components[offset + 1] = color.G;
         components[offset + 2] = color.B;
      }

      return new BridgeIpcFrameRequest(components, colors.Count, lampId);
   }
}
