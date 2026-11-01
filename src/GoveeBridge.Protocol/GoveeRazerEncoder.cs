using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

/// <summary>
/// Encodes binary commands for Govee's undocumented
/// 0xBB Razer real-time lighting protocol.
/// </summary>
/// <remarks>
/// This encoder implements the format experimentally
/// verified on the H6072.
///
/// It does not perform network I/O, manage device ownership,
/// or determine whether another Govee model supports Razer.
/// </remarks>
internal static class GoveeRazerEncoder
{
   /// <summary>
   /// Encodes a command to enable or disable the device's
   /// real-time lighting channel.
   /// </summary>
   /// <param name="enabled">
   /// True to arm real-time mode; false to disarm it.
   /// </param>
   /// <returns>A complete binary Razer command.</returns>
   public static byte[] EncodeEnabled(bool enabled)
   {
      byte[] payload = { enabled ? (byte)1 : (byte)0 };
      return Encode(_setEnabledOpcode, payload);
   }

   /// <summary>
   /// Encodes an RGB frame for real-time multizone lighting.
   /// </summary>
   /// <param name="frame">
   /// Ordered RGB values to transmit to the device.
   /// </param>
   /// <param name="gradient">
   /// Requests the firmware's gradient mode. False requests
   /// discrete regions, although physical or firmware blending
   /// may still be visible.
   /// </param>
   /// <returns>A complete binary Razer frame.</returns>
   /// <exception cref="ArgumentException">
   /// The frame is empty or exceeds the protocol's
   /// single-byte color-count limit.
   /// </exception>
   public static byte[] EncodeFrame(RgbFrame frame, bool gradient)
   {
      ArgumentNullException.ThrowIfNull(frame);
      IReadOnlyList<RgbColor> colors = frame.Colors;

      if (colors is null ||
          colors.Count < 1 ||
          colors.Count > _maximumColorCount)
      {
         throw new ArgumentException(
             $"A frame must contain 1 to {_maximumColorCount} colors.",
             nameof(frame));
      }

      byte[] payload = new byte[_frameMetadataLength + colors.Count * _bytesPerColor];

      payload[0] = gradient ? (byte)1 : (byte)0;
      payload[1] = checked((byte)colors.Count);

      for (int i = 0; i < colors.Count; i++)
      {
         RgbColor color = colors[i];
         int offset = _frameMetadataLength + i * _bytesPerColor;
         payload[offset] = color.R;
         payload[offset + 1] = color.G;
         payload[offset + 2] = color.B;
      }

      return Encode(_setColorsOpcode, payload);
   }

   /// <summary>
   /// Wraps an opcode and payload in a binary Razer packet.
   ///
   /// Layout:
   /// BB | payload length (16-bit big-endian) |
   /// opcode | payload | XOR checksum
   /// </summary>
   private static byte[] Encode(byte opcode, ReadOnlySpan<byte> payload)
   {
      if (payload.Length > ushort.MaxValue)
      {
         throw new ArgumentOutOfRangeException(nameof(payload));
      }

      byte[] packet = new byte[_headerLength + payload.Length + _checksumLength];
      packet[0] = _magic;
      packet[1] = (byte)(payload.Length >> 8);
      packet[2] = (byte)payload.Length;
      packet[3] = opcode;

      payload.CopyTo(packet.AsSpan(_headerLength));
      byte checksum = 0;
      for (int i = 0; i < packet.Length - _checksumLength; i++)
      {
         checksum ^= packet[i];
      }

      packet[^1] = checksum;
      return packet;
   }

   private const byte _magic = 0xBB;
   private const byte _setColorsOpcode = 0xB0;
   private const byte _setEnabledOpcode = 0xB1;

   private const int _headerLength = 4;
   private const int _checksumLength = 1;
   private const int _frameMetadataLength = 2;
   private const int _bytesPerColor = 3;
   private const int _maximumColorCount = byte.MaxValue;
}
