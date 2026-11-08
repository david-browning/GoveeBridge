namespace GoveeBridge.Core;

/// <summary>
/// A device client rejected a requested lamp parameter before submitting it
/// to the physical transport. This does not indicate a network or device fault.
/// </summary>
public sealed class LampParameterException : ArgumentException
{
   public LampParameterException(string message)
      : base(message)
   {
   }

   public LampParameterException(string message, string paramName)
      : base(message, paramName)
   {
   }

   public LampParameterException(
      string message, 
      string paramName, 
      Exception innerException)
      : base(message, paramName, innerException)
   {
   }
}
