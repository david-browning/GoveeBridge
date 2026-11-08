namespace GoveeBridge.Core;

/// <summary>
/// Describes the Host's chosen authority to control a lamp, not a physical
/// mode reported or acknowledged by the device.
/// </summary>
public enum ControlMode
{
   /// <summary>
   /// No unsolicited lighting output. A client can explicitly request PC control.
   /// </summary>
   Manual,

   /// <summary>
   /// Direct, discrete lighting commands are permitted; frames are not emitted.
   /// </summary>
   Pc,

   /// <summary>
   /// Real-time frames plus power and global brightness are permitted.
   /// </summary>
   PcStreaming
}
