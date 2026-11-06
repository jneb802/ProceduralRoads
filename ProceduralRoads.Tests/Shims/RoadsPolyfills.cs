// The compiler support type a positional record needs, which .NET Framework lacks (the doubles' Polyfills.cs
// supplies ModuleInitializerAttribute only): lets the net48 target compile the staircase tests' records.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
