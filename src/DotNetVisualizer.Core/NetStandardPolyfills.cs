// Small gaps of netstandard2.0 compared with current .NET, so the shared code reads the same everywhere.
namespace DotNetVisualizer.Core;

internal static class KeyValuePairExtensions
{
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }
}
