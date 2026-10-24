using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetVisualizer.Web.Models;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    /// <summary>Reads exact NuGet package versions from a .NET dependency manifest (*.deps.json).</summary>
    public static class DepsJsonReader
    {
        public static List<ScanComponent> Read(Stream json, string source)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var result = new List<ScanComponent>();

            // Which packages really contribute an assembly? (packages the shared framework supplies have no runtime assets)
            var ships = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Object)
            {
                foreach (var target in targets.EnumerateObject())
                {
                    foreach (var entry in target.Value.EnumerateObject())
                    {
                        bool has = entry.Value.ValueKind == JsonValueKind.Object &&
                                   (HasAny(entry.Value, "runtime") || HasAny(entry.Value, "native") || HasAny(entry.Value, "runtimeTargets"));
                        ships[entry.Name] = ships.TryGetValue(entry.Name, out var prev) ? prev || has : has;
                    }
                    break;                                         // the first target is the one the app runs on
                }
            }

            if (!doc.RootElement.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("This file has no \"libraries\" section - it does not look like a .deps.json dependency manifest.");

            foreach (var lib in libs.EnumerateObject())
            {
                // keys look like "System.Text.Json/8.0.0"; project references and runtime packs are not NuGet packages
                var type = lib.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase)) continue;

                var slash = lib.Name.LastIndexOf('/');
                if (slash <= 0 || slash == lib.Name.Length - 1) continue;
                bool? shipsCode = ships.Count > 0 && ships.TryGetValue(lib.Name, out var s) ? s : null;
                result.Add(new ScanComponent("NuGet", lib.Name.Substring(0, slash), lib.Name.Substring(slash + 1), source, "confirmed", shipsCode));
            }
            return result;
        }

        private static bool HasAny(JsonElement entry, string property) =>
            entry.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Object && p.EnumerateObject().Any();
    }

    /// <summary>Lists the assemblies a managed assembly references. Only the assembly version is known, not the NuGet package version.</summary>
    public static class AssemblyReferenceReader
    {
        public static List<ReferencedAssembly> Read(MetadataReader reader)
        {
            var list = new List<ReferencedAssembly>();
            foreach (var handle in reader.AssemblyReferences)
            {
                var r = reader.GetAssemblyReference(handle);
                list.Add(new ReferencedAssembly { Name = reader.GetString(r.Name), Version = r.Version.ToString() });
            }
            return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Reads the version of a bundled JavaScript library from its banner comment.</summary>
    public static class BundledScriptReader
    {
        private static readonly Regex VersionLine = new(@"@version\s+([0-9A-Za-z.\-+]+)", RegexOptions.Compiled);

        public static ScanComponent? ReadBanner(string packageName, string headText)
        {
            var m = VersionLine.Match(headText);
            return m.Success ? new ScanComponent("npm", packageName, m.Groups[1].Value, "bundled-script") : null;
        }
    }
}
