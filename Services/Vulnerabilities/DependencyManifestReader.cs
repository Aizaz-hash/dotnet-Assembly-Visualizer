using System.Text.Json;
using DotNetVisualizer.Web.Models;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    /// <summary>
    /// Reads the exact package set - and who depends on whom - from any of the manifests a .NET project produces:
    /// <c>*.deps.json</c> (what a published app ships), <c>obj/project.assets.json</c> (the full restore result, including
    /// indirect packages) and <c>packages.lock.json</c> (NuGet lock file). Versions are exact; every package gets its dependency
    /// level (1 = referenced by the project itself, 2 = a dependency of a level-1 package, ...) and the path that reaches it.
    /// </summary>
    public static class DependencyManifestReader
    {
        public static List<ScanComponent> Read(Stream json, string source)
        {
            using var ms = new MemoryStream();
            json.CopyTo(ms);
            var bytes = ms.ToArray();
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("This file is not a dependency manifest (.deps.json, project.assets.json or packages.lock.json).");

            bool isLock = root.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Object && !root.TryGetProperty("libraries", out _);
            if (isLock) return ReadLockFile(d, source);

            // .deps.json and project.assets.json share the "libraries" + "targets" layout
            var flat = DepsJsonReader.Read(new MemoryStream(bytes), source);
            return AddLevels(root, flat);
        }

        // ---------------------------------------------------------------- deps.json / project.assets.json

        private static List<ScanComponent> AddLevels(JsonElement root, List<ScanComponent> flat)
        {
            if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object) return flat;

            string? wanted = root.TryGetProperty("runtimeTarget", out var rt) && rt.TryGetProperty("name", out var rn) ? rn.GetString() : null;
            JsonElement? target = null;
            foreach (var t in targets.EnumerateObject())
            {
                if (wanted != null && t.Name == wanted) { target = t.Value; break; }
                target ??= t.Value;
            }
            if (target is not { ValueKind: JsonValueKind.Object } tv) return flat;

            // name -> "Name/1.2.3" as it appears in the target, and each entry's dependency names
            var keyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tv.EnumerateObject())
            {
                int slash = entry.Name.LastIndexOf('/');
                if (slash <= 0) continue;
                keyOf[entry.Name.Substring(0, slash)] = entry.Name;
                var list = new List<string>();
                if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                    list.AddRange(deps.EnumerateObject().Select(x => x.Name));
                edges[entry.Name.Substring(0, slash)] = list;
            }

            // roots: what the project itself asks for
            var roots = new List<string>();
            string projectName = "project";
            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Object)
            {
                foreach (var lib in libs.EnumerateObject())
                {
                    if (!lib.Value.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "project", StringComparison.OrdinalIgnoreCase)) continue;
                    int slash = lib.Name.LastIndexOf('/');
                    projectName = slash > 0 ? lib.Name.Substring(0, slash) : lib.Name;
                    if (edges.TryGetValue(projectName, out var projectDeps)) roots.AddRange(projectDeps);   // deps.json lists the app in its own target
                }
            }
            if (root.TryGetProperty("projectFileDependencyGroups", out var groups) && groups.ValueKind == JsonValueKind.Object)
            {
                foreach (var g in groups.EnumerateObject())
                {
                    foreach (var line in g.Value.EnumerateArray())
                        if (line.GetString()?.Split(' ', 2)[0] is { Length: > 0 } name) roots.Add(name);
                    break;                                                                              // first framework
                }
            }
            if (roots.Count == 0)
            {
                // no explicit roots: packages nobody else depends on
                var referenced = new HashSet<string>(edges.Values.SelectMany(x => x), StringComparer.OrdinalIgnoreCase);
                roots.AddRange(edges.Keys.Where(k => !referenced.Contains(k)));
            }

            var info = Bfs(roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(keyOf.ContainsKey).ToList(), edges, projectName);
            return flat.Select(c => info.TryGetValue(c.Name, out var li) ? c with { Level = li.Level, Via = li.Via } : c).ToList();
        }

        // ---------------------------------------------------------------- packages.lock.json

        private static List<ScanComponent> ReadLockFile(JsonElement dependencies, string source)
        {
            var result = new List<ScanComponent>();
            foreach (var framework in dependencies.EnumerateObject())
            {
                if (framework.Value.ValueKind != JsonValueKind.Object) continue;

                var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var roots = new List<string>();
                foreach (var pkg in framework.Value.EnumerateObject())
                {
                    string type = pkg.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (type.Equals("Project", StringComparison.OrdinalIgnoreCase)) continue;      // another project of the solution, not a package
                    if (!pkg.Value.TryGetProperty("resolved", out var resolved) || string.IsNullOrWhiteSpace(resolved.GetString())) continue;
                    versions[pkg.Name] = resolved.GetString()!;
                    edges[pkg.Name] = pkg.Value.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object
                        ? deps.EnumerateObject().Select(x => x.Name).ToList() : new List<string>();
                    if (type.Equals("Direct", StringComparison.OrdinalIgnoreCase)) roots.Add(pkg.Name);
                }

                var info = Bfs(roots.Where(versions.ContainsKey).ToList(), edges, "project");
                foreach (var (name, version) in versions)
                {
                    info.TryGetValue(name, out var li);
                    result.Add(new ScanComponent("NuGet", name, version, source, "confirmed", null, li.Level == 0 ? null : li.Level, li.Via));
                }
                break;                                                                             // the first target framework is enough: the packages are the same
            }
            if (result.Count == 0)
                throw new InvalidDataException("This file has no package entries - it does not look like a packages.lock.json.");
            return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---------------------------------------------------------------- shared

        private static Dictionary<string, (int Level, string Via)> Bfs(List<string> roots, Dictionary<string, List<string>> edges, string rootName)
        {
            var info = new Dictionary<string, (int Level, string Via)>(StringComparer.OrdinalIgnoreCase);
            var frontier = new List<string>();
            foreach (var r in roots) if (info.TryAdd(r, (1, rootName + " → " + r))) frontier.Add(r);

            for (int level = 2; frontier.Count > 0; level++)
            {
                var next = new List<string>();
                foreach (var parent in frontier)
                {
                    if (!edges.TryGetValue(parent, out var children)) continue;
                    foreach (var child in children)
                        if (edges.ContainsKey(child) && info.TryAdd(child, (level, info[parent].Via + " → " + child))) next.Add(child);   // each package once, at its shortest path; cycles end here
                }
                frontier = next;
            }
            return info;
        }
    }
}
