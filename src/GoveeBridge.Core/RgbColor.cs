namespace GoveeBridge.Core
{
   public readonly record struct RgbColor(byte R, byte G, byte B)
   {
      public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
   }
}
