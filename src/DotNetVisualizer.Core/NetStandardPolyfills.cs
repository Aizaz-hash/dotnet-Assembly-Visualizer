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

internal static class StringPolyfills
{
    public static string[] Split(this string s, char separator, StringSplitOptions options) => s.Split(new[] { separator }, options);
    public static string[] Split(this string s, char separator, int count) => s.Split(new[] { separator }, count);
    public static string[] Split(this string s, string separator, StringSplitOptions options) => s.Split(new[] { separator }, options);
}

internal static class CollectionPolyfills
{
    public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source, IEqualityComparer<T> comparer) => new HashSet<T>(source, comparer);

    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.ContainsKey(key)) return false;
        dictionary.Add(key, value);
        return true;
    }
}

internal static class TaskPolyfills
{
    /// <summary>Completes when the task does, or throws <see cref="OperationCanceledException"/> as soon as the token is cancelled.</summary>
    public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted) return await task.ConfigureAwait(false);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) throw new OperationCanceledException(cancellationToken);
        }
        return await task.ConfigureAwait(false);
    }
}

internal static class HttpContentPolyfills
{
    public static Task<Stream> ReadAsStreamAsync(this System.Net.Http.HttpContent content, CancellationToken cancellationToken) => content.ReadAsStreamAsync().WaitAsync(cancellationToken);
    public static Task<string> ReadAsStringAsync(this System.Net.Http.HttpContent content, CancellationToken cancellationToken) => content.ReadAsStringAsync().WaitAsync(cancellationToken);
}
