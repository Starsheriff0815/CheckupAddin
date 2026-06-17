#if NET48
// Polyfill that allows C# 9 `init` property setters to compile on .NET Framework 4.8,
// where System.Runtime.CompilerServices.IsExternalInit is not shipped by the runtime.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
