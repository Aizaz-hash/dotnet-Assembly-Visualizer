using DotNetVisualizer.Web.Models;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    public sealed class DependencyRequest
    {
        /// <summary>Display name of level 0 (the uploaded assembly).</summary>
        public string RootName { get; init; } = "assembly";
        public IReadOnlyList<ReferencedAssembly> References { get; init; } = Array.Empty<ReferencedAssembly>();
        /// <summary>From the assembly's TargetFrameworkAttribute (".NETStandard,Version=v2.0"), or null.</summary>
        public string? TargetFramework { get; init; }
        /// <summary>Package id -> exact version, from a manifest the user supplied (.deps.json). Wins over every estimate.</summary>
        public IReadOnlyDictionary<string, string>? ExactVersions { get; init; }
        /// <summary>Deepest level to resolve: 1 = direct references only ... 4. Level 0 is the uploaded assembly.</summary>
        public int MaxLevel { get; init; } = DependencyResolver.MaxSupportedLevel;
    }

    public sealed class DependencyNode
    {
        public int Level { get; init; }
        public string Name { get; init; } = "";
        public string? Version { get; set; }
        /// <summary>"exact", "nuspec-minimum", "assembly-estimate" or "none".</summary>
        public string VersionSource { get; set; } = "none";
        public string? AssemblyName { get; init; }
        public string? AssemblyVersion { get; init; }
        public string Via { get; init; } = "";
        public int AlsoRequiredBy { get; set; }
        /// <summary>"resolved", "framework", "unresolved" or "lookup-unavailable".</summary>
        public string Resolution { get; set; } = "resolved";
        public bool PackageIdentified { get; set; }
        public bool VersionIdentified { get; set; }
        internal bool Expanded { get; set; }
    }

    public sealed class DependencyResolution
    {
        public List<DependencyNode> Nodes { get; } = new();
        public List<string> Notes { get; } = new();
        public int MaxLevelResolved { get; set; }
    }

    public interface IDependencyResolver
    {
        Task<DependencyResolution> ResolveAsync(DependencyRequest request, CancellationToken ct);
    }

    /// <summary>
    /// Walks an assembly's dependencies without any manifest: assembly references (level 1) are matched to NuGet packages,
    /// then each package's .nuspec supplies the next level, down to <see cref="MaxSupportedLevel"/>. Every package appears once
    /// (at its shortest distance), cycles end where a package was already seen, and anything that cannot be resolved is kept
    /// and labelled instead of being dropped.
    /// </summary>
    public sealed class DependencyResolver : IDependencyResolver
    {
        public const int MaxSupportedLevel = 4;
        private const int MaxNodes = 400;
        private const int Parallelism = 8;

        // Always supplied by the runtime itself, whatever the target framework.
        private static readonly HashSet<string> CoreFramework = new(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib", "netstandard", "System", "System.Runtime", "System.Private.CoreLib", "WindowsBase"
        };

        private static readonly Lazy<HashSet<string>> RuntimeAssemblies = new(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (tpa != null)
                foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    set.Add(Path.GetFileNameWithoutExtension(path));
            return set;
        });

        private readonly INuGetClient _nuget;
        private readonly Func<string, bool> _isRuntimeAssembly;

        public DependencyResolver(INuGetClient nuget, Func<string, bool>? isRuntimeAssembly = null)
        {
            _nuget = nuget;
            _isRuntimeAssembly = isRuntimeAssembly ?? (name => RuntimeAssemblies.Value.Contains(name));
        }

        public async Task<DependencyResolution> ResolveAsync(DependencyRequest request, CancellationToken ct)
        {
            var result = new DependencyResolution();
            int maxLevel = Math.Clamp(request.MaxLevel, 1, MaxSupportedLevel);
            var tfm = NuGetVersioning.ParseTfm(request.TargetFramework);
            var gate = new SemaphoreSlim(Parallelism);
            var seen = new Dictionary<string, DependencyNode>(StringComparer.OrdinalIgnoreCase);   // "id@version" -> node
            bool capped = false, nugetDown = false;

            // ---------------- level 1: what the uploaded assembly references ------------------------------------------
            var level = new List<DependencyNode>();
            var refs = request.References
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.Version, Comparer<string>.Create(VersionComparer.Compare)).First())
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var resolveTasks = new List<Task<DependencyNode>>();
            foreach (var r in refs)
            {
                string? exact = null;
                bool exactKnown = request.ExactVersions != null && request.ExactVersions.TryGetValue(r.Name, out exact);

                if (!exactKnown && IsFramework(r.Name, tfm))
                {
                    level.Add(new DependencyNode
                    {
                        Level = 1, Name = r.Name, AssemblyName = r.Name, AssemblyVersion = r.Version, Via = request.RootName + " → " + r.Name,
                        Resolution = "framework", PackageIdentified = false, VersionIdentified = false
                    });
                    continue;
                }
                resolveTasks.Add(ResolveReferenceAsync(r, exact, request.RootName, gate, ct));
            }
            level.AddRange(await Task.WhenAll(resolveTasks));

            foreach (var n in level)
            {
                if (n.Resolution == "lookup-unavailable") nugetDown = true;
                Register(seen, n, result, ref capped);
            }
            result.MaxLevelResolved = level.Count > 0 ? 1 : 0;

            // ---------------- levels 2..maxLevel: dependencies declared by each package -------------------------------
            for (int depth = 1; depth < maxLevel && level.Count > 0; depth++)
            {
                var expandable = level.Where(n => n.VersionIdentified && n.Resolution == "resolved" && !n.Expanded).ToList();
                var children = new List<DependencyNode>();
                var childTasks = expandable.Select(async parent =>
                {
                    parent.Expanded = true;
                    NuspecInfo? info;
                    await gate.WaitAsync(ct);
                    try { info = await _nuget.GetNuspecAsync(parent.Name, parent.Version!, ct); }
                    catch (NuGetUnavailableException) { nugetDown = true; lock (result) result.Notes.Add($"The dependencies of {parent.Name} {parent.Version} could not be read because NuGet could not be reached."); return; }
                    finally { gate.Release(); }

                    if (info == null)
                    {
                        lock (result) result.Notes.Add($"No package metadata was found for {parent.Name} {parent.Version}, so its own dependencies are unknown.");
                        return;
                    }

                    var group = NuGetVersioning.SelectGroup(info.Groups, request.TargetFramework);
                    if (group == null) return;
                    foreach (var dep in group.Dependencies)
                    {
                        string? min = NuGetVersioning.MinimumVersion(dep.VersionRange);
                        var child = new DependencyNode
                        {
                            Level = parent.Level + 1, Name = dep.Id, AssemblyName = dep.Id, Version = min,
                            VersionSource = min == null ? "none" : "nuspec-minimum",
                            Via = parent.Via + " → " + dep.Id,
                            Resolution = "resolved", PackageIdentified = true, VersionIdentified = min != null
                        };
                        if (min == null) child.Resolution = "unresolved";
                        lock (children) children.Add(child);
                    }
                }).ToList();
                await Task.WhenAll(childTasks);

                // register in a stable order so the shortest/first path wins deterministically
                level = new List<DependencyNode>();
                foreach (var child in children.OrderBy(c => c.Via, StringComparer.OrdinalIgnoreCase))
                {
                    if (Register(seen, child, result, ref capped)) level.Add(child);
                }
                if (level.Count > 0) result.MaxLevelResolved = depth + 1;
            }

            // packages at the last level may declare more; say so instead of silently stopping
            var lastLevelOpen = result.Nodes.Where(n => n.Level == maxLevel && n.VersionIdentified && n.Resolution == "resolved" && !n.Expanded).ToList();
            if (lastLevelOpen.Count > 0)
                result.Notes.Add($"Dependencies were followed to level {maxLevel}; the {lastLevelOpen.Count} package(s) at that level were checked but their own dependencies were not expanded.");
            if (capped) result.Notes.Add($"Only the first {MaxNodes} dependencies were resolved; the rest were skipped.");
            if (nugetDown) result.Notes.Add("NuGet could not be reached for some packages; they are marked \"lookup unavailable\" and were not checked. This must not be read as \"no vulnerabilities\".");
            return result;
        }

        private bool IsFramework(string assemblyName, NuGetVersioning.Tfm? tfm) =>
            CoreFramework.Contains(assemblyName) || (_isRuntimeAssembly(assemblyName) && NuGetVersioning.IsModernDotNet(tfm));

        /// <summary>Adds the node, or records an extra "required by" on the existing one. Returns true when the node is new.</summary>
        private static bool Register(Dictionary<string, DependencyNode> seen, DependencyNode node, DependencyResolution result, ref bool capped)
        {
            string key = node.Name + "@" + (node.Version ?? "?");
            if (seen.TryGetValue(key, out var existing))
            {
                existing.AlsoRequiredBy++;
                return false;
            }
            if (seen.Count >= MaxNodes) { capped = true; return false; }
            seen[key] = node;
            result.Nodes.Add(node);
            return true;
        }

        private async Task<DependencyNode> ResolveReferenceAsync(ReferencedAssembly r, string? exactVersion, string rootName, SemaphoreSlim gate, CancellationToken ct)
        {
            var node = new DependencyNode
            {
                Level = 1, Name = r.Name, AssemblyName = r.Name, AssemblyVersion = r.Version, Via = rootName + " → " + r.Name
            };

            if (exactVersion != null)
            {
                node.Version = exactVersion; node.VersionSource = "exact";
                node.PackageIdentified = true; node.VersionIdentified = true;
                return node;
            }

            IReadOnlyList<string>? versions;
            await gate.WaitAsync(ct);
            try { versions = await _nuget.GetVersionsAsync(r.Name, ct); }
            catch (NuGetUnavailableException)
            {
                node.Resolution = "lookup-unavailable";
                return node;
            }
            finally { gate.Release(); }

            if (versions == null || versions.Count == 0)
            {
                node.Resolution = "unresolved";          // not a NuGet package we can find (private/in-house assembly, or a differently named package)
                return node;
            }

            node.PackageIdentified = true;
            var estimate = NuGetVersioning.EstimatePackageVersion(versions, r.Version);
            if (estimate == null)
            {
                node.Resolution = "unresolved";
                return node;
            }
            node.Version = estimate; node.VersionSource = "assembly-estimate"; node.VersionIdentified = true;
            return node;
        }
    }
}
