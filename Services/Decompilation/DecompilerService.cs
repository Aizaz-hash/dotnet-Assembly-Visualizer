using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace DotNetVisualizer.Web.Services.Decompilation;

public interface IDecompilerService
{
    /// <summary>Keeps the assembly for a while and returns an id to ask for source with. The same bytes always give the same id.</summary>
    string Open(byte[] assembly);

    /// <summary>C# for a type (token 0x02......) or a method (token 0x06......), or null when the id has expired or the token is not in the assembly.</summary>
    Task<DecompiledSource?> DecompileAsync(string id, int token, CancellationToken ct);
}

public sealed record DecompiledSource(string Code, bool Truncated, string Kind, string Name);

/// <summary>
/// C# for the selected type or method, produced by ICSharpCode.Decompiler (the engine behind ILSpy, MIT licence).
/// Assemblies are held in memory for a short while so clicking through methods does not upload or re-parse the file each time.
/// Nothing is executed: the decompiler only reads IL.
/// </summary>
public sealed class DecompilerService : IDecompilerService
{
    public const int MaxCharacters = 400_000;
    private const int MaxOpenAssemblies = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public required CSharpDecompiler Decompiler { get; init; }
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public readonly SemaphoreSlim Gate = new(1, 1);          // the decompiler is not thread-safe
    }

    private readonly Dictionary<string, Entry> _open = new();
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;

    public DecompilerService(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public string Open(byte[] assembly)
    {
        string id = Convert.ToHexString(SHA256.HashData(assembly)).Substring(0, 24).ToLowerInvariant();
        lock (_lock)
        {
            Evict();
            if (_open.TryGetValue(id, out var existing)) { existing.LastUsedUtc = _now(); return id; }
        }

        // building the type system can take a while for a big assembly: do it outside the lock
        var module = new PEFile("assembly", new MemoryStream(assembly, writable: false), PEStreamOptions.PrefetchEntireImage);
        if (!module.IsAssembly && module.Metadata.TypeDefinitions.Count == 0)
            throw new InvalidDataException("The file does not contain a .NET assembly.");

        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false, LoadInMemory = true };
        var decompiler = new CSharpDecompiler(module, new RuntimeAssemblyResolver(), settings);
        lock (_lock)
        {
            _open[id] = new Entry { Decompiler = decompiler, LastUsedUtc = _now() };
            Evict();
        }
        return id;
    }

    public async Task<DecompiledSource?> DecompileAsync(string id, int token, CancellationToken ct)
    {
        Entry? entry;
        lock (_lock)
        {
            Evict();
            if (!_open.TryGetValue(id, out entry)) return null;
            entry.LastUsedUtc = _now();
        }

        var handle = MetadataTokens.EntityHandle(token);
        var metadata = entry.Decompiler.TypeSystem.MainModule.MetadataFile.Metadata;
        bool isType = handle.Kind == System.Reflection.Metadata.HandleKind.TypeDefinition;
        bool isMethod = handle.Kind == System.Reflection.Metadata.HandleKind.MethodDefinition;
        if (handle.IsNil || (!isType && !isMethod)) return null;                                     // only types and methods are decompiled
        int rows = metadata.GetTableRowCount(isType ? TableIndex.TypeDef : TableIndex.MethodDef);
        if (MetadataTokens.GetRowNumber(handle) > rows) return null;

        await entry.Gate.WaitAsync(ct);
        try
        {
            string code;
            try
            {
                code = await Task.Run(() => entry.Decompiler.DecompileAsString(handle), ct).WaitAsync(Budget, ct);
            }
            catch (TimeoutException)
            {
                return new DecompiledSource($"// Decompiling this took longer than {Budget.TotalSeconds:0} seconds and was stopped.\n// Try a single method instead of the whole type.", true, Kind(handle), "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new DecompiledSource("// The decompiler could not produce C# for this member:\n// " + ex.Message.Replace("\n", "\n// "), false, Kind(handle), "");
            }

            bool truncated = code.Length > MaxCharacters;
            if (truncated) code = code.Substring(0, MaxCharacters) + "\n// … output cut at " + MaxCharacters / 1000 + " 000 characters. Open a single method for the rest.";
            return new DecompiledSource(code, truncated, Kind(handle), "");
        }
        finally { entry.Gate.Release(); }
    }

    private static string Kind(System.Reflection.Metadata.EntityHandle h) => h.Kind == System.Reflection.Metadata.HandleKind.TypeDefinition ? "type" : "method";

    private void Evict()
    {
        var now = _now();
        foreach (var stale in _open.Where(kv => now - kv.Value.LastUsedUtc > Lifetime).Select(kv => kv.Key).ToList()) _open.Remove(stale);
        while (_open.Count > MaxOpenAssemblies)
            _open.Remove(_open.OrderBy(kv => kv.Value.LastUsedUtc).First().Key);
    }

    /// <summary>
    /// Finds referenced assemblies in the .NET runtime that runs this server, so base types and framework members resolve to names.
    /// Anything it cannot find is left unresolved - the decompiler still produces readable C#, with less precise types.
    /// </summary>
    private sealed class RuntimeAssemblyResolver : IAssemblyResolver
    {
        private static readonly string[] Directories = Candidates().Where(Directory.Exists).ToArray();

        private static IEnumerable<string> Candidates()
        {
            yield return System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var shared = Path.GetDirectoryName(Path.GetDirectoryName(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)));
            if (shared != null)
            {
                var aspnet = Path.Combine(shared, "Microsoft.AspNetCore.App");
                if (Directory.Exists(aspnet))
                    foreach (var dir in Directory.GetDirectories(aspnet).OrderByDescending(d => d, StringComparer.Ordinal).Take(1)) yield return dir;
            }
        }

        private readonly Dictionary<string, MetadataFile?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public MetadataFile? Resolve(IAssemblyReference reference)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(reference.Name, out var hit)) return hit;
                MetadataFile? found = null;
                foreach (var dir in Directories)
                {
                    var path = Path.Combine(dir, reference.Name + ".dll");
                    if (!File.Exists(path)) continue;
                    try { found = new PEFile(path, PEStreamOptions.PrefetchMetadata); break; } catch (Exception) { /* not a managed file: keep looking */ }
                }
                return _cache[reference.Name] = found;
            }
        }

        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;
        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);
    }
}
