// Lets netstandard2.0 compile records and init-only properties; the type is only needed at compile time.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
