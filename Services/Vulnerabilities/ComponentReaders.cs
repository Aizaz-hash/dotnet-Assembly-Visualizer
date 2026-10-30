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

        /// <summary>A reference assembly (what the compiler reads, [ReferenceAssembly]) has the API but no real code: there is no behaviour to analyse.</summary>
        public static bool IsReferenceAssembly(MetadataReader reader)
        {
            if (!reader.IsAssembly) return false;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(handle);
                string? attributeType = null;
                if (attr.Constructor.Kind == HandleKind.MemberReference)
                {
                    // the attribute type lives in another assembly (normal case)
                    var parent = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
                    if (parent.Kind == HandleKind.TypeReference) attributeType = reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name);
                }
                else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
                {
                    // the assembly that defines the attribute itself (System.Runtime) uses it from its own metadata
                    var owner = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
                    attributeType = reader.GetString(reader.GetTypeDefinition(owner).Name);
                }
                if (attributeType == "ReferenceAssemblyAttribute") return true;
            }
            return false;
        }

        /// <summary>The assembly's own name (level 0 in the dependency graph).</summary>
        public static string ReadAssemblyName(MetadataReader reader) =>
            reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : "";

        /// <summary>Reads [TargetFramework(".NETStandard,Version=v2.0")] straight from the metadata, or null when absent.</summary>
        public static string? ReadTargetFramework(MetadataReader reader)
        {
            if (!reader.IsAssembly) return null;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(handle);
                string? typeName = null, typeNs = null;
                if (attr.Constructor.Kind == HandleKind.MemberReference)
                {
                    var parent = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
                    if (parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = reader.GetTypeReference((TypeReferenceHandle)parent);
                        typeName = reader.GetString(tr.Name); typeNs = reader.GetString(tr.Namespace);
                    }
                }
                if (typeName != "TargetFrameworkAttribute" || typeNs != "System.Runtime.Versioning") continue;

                // blob: 0x0001 prolog, then the first fixed argument = a SerString (compressed length + UTF-8)
                var blob = reader.GetBlobReader(attr.Value);
                if (blob.Length < 3 || blob.ReadUInt16() != 1) return null;
                return blob.ReadSerializedString();
            }
            return null;
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
