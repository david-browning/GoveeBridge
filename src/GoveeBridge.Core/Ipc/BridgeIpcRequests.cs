namespace GoveeBridge.Core.Ipc;

/// <summary>
/// Factory methods for the Host's supported and planned IPC request messages.
/// </summary>
/// <remarks>
/// These methods construct transport-independent DTOs. They do not open sockets,
/// take control of the lamp, or guarantee that a Host implements a new command.
/// Serialize a returned request with System.Text.Json before sending it.
///
/// Management commands use 'type=command' plus an action and optional data.
/// RGB streaming uses 'type=rgb-frame', preserving the existing fast receive path.
/// </remarks>
public static class BridgeIpcRequests
{
   /// <summary>Requests the Host's cached status and ownership snapshot.</summary>
   public static BridgeIpcCommandRequest Status() =>
      new(BridgeIpcActions.GetHostStatus);

   /// <summary>
   /// Requests a physical lamp status observation through the Host, when
   /// supported by the current operating mode. May time out or be unavailable.
   /// </summary>
   public static BridgeIpcCommandRequest LampStatus() =>
      new(BridgeIpcActions.GetLampStatus);

   /// <summary>
   /// Asks the Host to grant PC control. The command does not itself set a color
   /// and the caller must supply a fresh frame after ownership is granted.
   /// </summary>
   public static BridgeIpcCommandRequest Resume() =>
      new(BridgeIpcActions.Resume);

   /// <summary>
   /// Asks the Host to return to manual control without intentionally powering
   /// off or overwriting the lamp's appearance.
   /// </summary>
   public static BridgeIpcCommandRequest Release() =>
      new(BridgeIpcActions.Release);

   /// <summary>
   /// Requests a power change. The Host must enforce ownership rules before
   /// touching hardware; this request does not implicitly claim ownership.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcPowerPayload> SetPower(bool isOn) =>
      new(BridgeIpcActions.SetPower, new BridgeIpcPowerPayload { IsOn = isOn });

   /// <summary>
   /// Requests a global brightness percentage independently of color.
   /// </summary>
   /// <remarks>
   /// Zero matches the domain LampState range (0..100), even though the Govee
   /// LAN brightness command accepts only 1..100. Mapping zero is a Host policy
   /// decision that must be specified before implementing dispatch.
   /// </remarks>
   public static BridgeIpcCommandRequest<BridgeIpcBrightnessPayload> SetBrightness(
      int brightnessPercent)
   {
      ArgumentOutOfRangeException.ThrowIfNegative(brightnessPercent);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(brightnessPercent, 100);
      return new(BridgeIpcActions.SetBrightness,
         new BridgeIpcBrightnessPayload { BrightnessPercent = brightnessPercent });
   }

   /// <summary>
   /// Requests a static, solid RGB color. This differs from SendFrame: a color
   /// change is a management request, not an expiring animation sample.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcColorPayload> SetColor(RgbColor color) =>
      new(BridgeIpcActions.SetSolidColor, new BridgeIpcColorPayload
      {
         Color = new BridgeIpcRgbColor(color.R, color.G, color.B)
      });

   /// <summary>
   /// Requests white-light color temperature. Any device-specific Kelvin bounds
   /// must be validated by the Host and its selected device client.
   /// </summary>
   public static BridgeIpcCommandRequest<BridgeIpcColorTemperaturePayload>
      SetColorTemperature(int kelvin)
   {
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(kelvin);
      return new(BridgeIpcActions.SetColorTemperature,
         new BridgeIpcColorTemperaturePayload { Kelvin = kelvin });
   }

   /// <summary>
   /// Builds one complete RGB streaming frame, keeping the Host's existing
   /// 'rgb-frame' wire representation and flattening RGB triplets in order.
   /// </summary>
   /// <param name="frame">The logical frame to send.</param>
   /// <returns>A serializable frame message, not an applied lighting state.</returns>
   public static BridgeIpcFrameRequest SendFrame(RgbFrame frame)
   {
      ArgumentNullException.ThrowIfNull(frame);
      IReadOnlyList<RgbColor> colors = frame.Colors;
      if (colors is null || colors.Count is < 1 or > 256)
      {
         throw new ArgumentException("A frame must contain 1 to 256 colors.", nameof(frame));
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

      return new BridgeIpcFrameRequest(components, colors.Count);
   }
}
