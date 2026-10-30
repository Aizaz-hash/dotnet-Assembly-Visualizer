using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetVisualizer.Web.Services;

/// <summary>
/// Facts about an assembly that are not part of the graphs: what it is (name, version, kind, bitness, signed, debug build) and whether it
/// shows signs of obfuscation. Both come from metadata only; the obfuscation result is a set of indicators with the evidence behind
/// each, never a verdict - a clean result is not proof that nothing is obfuscated.
/// </summary>
public static class AssemblyInsights
{
    // Attributes that obfuscators and protectors stamp on what they produce (matched by name, so no reference to them is needed).
    private static readonly HashSet<string> ObfuscatorMarkers = new(StringComparer.Ordinal)
    {
        "ObfuscatedByAttribute", "ObfuscationAttribute", "DotfuscatorAttribute", "ConfusedByAttribute", "SmartAssembly.Attributes.PoweredByAttribute",
        "PoweredByAttribute", "SuppressIldasmAttribute", "AssemblyObfuscatedAttribute", "BabelObfuscatorAttribute", "CryptoObfuscator.ProtectedWithCryptoObfuscatorAttribute",
        "ProtectedWithCryptoObfuscatorAttribute", "AgileDotNetRT.Attribute"
    };

    // Short names that are ordinary C# and must not count as obfuscated.
    private static readonly HashSet<string> OrdinaryShortNames = new(StringComparer.Ordinal)
    {
        "Id", "ID", "Ok", "OK", "Do", "Go", "On", "To", "Is", "As", "Of", "If", "Or", "X", "Y", "Z", "T", "U", "V", "W", "H", "IO", "UI", "Db", "DB"
    };

    public static object ReadInfo(PEReader pe, MetadataReader md, long fileSize, string? targetFramework, int referenceCount)
    {
        var asm = md.IsAssembly ? md.GetAssemblyDefinition() : default;
        var headers = pe.PEHeaders;
        var cor = headers.CorHeader;
        bool is64 = headers.PEHeader?.Magic == PEMagic.PE32Plus;
        bool ilOnly = cor != null && (cor.Flags & CorFlags.ILOnly) != 0;
        bool requires32 = cor != null && (cor.Flags & CorFlags.Requires32Bit) != 0;

        return new
        {
            name = md.IsAssembly ? md.GetString(asm.Name) : "",
            version = md.IsAssembly ? asm.Version.ToString() : "",
            culture = md.IsAssembly && !asm.Culture.IsNil ? md.GetString(asm.Culture) : "neutral",
            kind = headers.IsExe ? "Executable (.exe)" : "Library (.dll)",
            platform = ilOnly ? (requires32 ? "x86 only" : "Any CPU") : (is64 ? "x64 (contains native code)" : "x86 (contains native code)"),
            managedOnly = ilOnly,
            strongNamed = md.IsAssembly && !asm.PublicKey.IsNil,
            hasEntryPoint = cor != null && cor.EntryPointTokenOrRelativeVirtualAddress != 0 && (cor.Flags & CorFlags.NativeEntryPoint) == 0,
            debugBuild = HasAttribute(md, "DebuggableAttribute") && HasDebugInfoDirectory(pe),
            targetFramework,
            references = referenceCount,
            sizeBytes = fileSize,
            metadataVersion = md.MetadataVersion
        };
    }

    public static object DetectObfuscation(MetadataReader md)
    {
        int total = 0, shortNames = 0, odd = 0;
        var examples = new List<string>();

        void Consider(string name)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("<") || name.StartsWith(".") || name.Contains('`') && name.Length < 3) return;
            total++;
            if (HasUnusualCharacters(name))
            {
                odd++;
                if (examples.Count < 6) examples.Add(Printable(name));
            }
            else if (name.Length <= 2 && !OrdinaryShortNames.Contains(name) && name.All(char.IsLetter))
            {
                shortNames++;
                if (examples.Count < 6) examples.Add(name);
            }
        }

        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            string typeName = md.GetString(t.Name);
            if (typeName == "<Module>" || typeName.StartsWith("<")) continue;           // compiler generated: not a naming choice
            Consider(typeName);
            foreach (var mh in t.GetMethods()) Consider(md.GetString(md.GetMethodDefinition(mh).Name));
            foreach (var fh in t.GetFields()) Consider(md.GetString(md.GetFieldDefinition(fh).Name));
        }

        var markers = new List<string>();
        void ScanAttributes(CustomAttributeHandleCollection attributes)
        {
            foreach (var h in attributes)
            {
                var ctor = md.GetCustomAttribute(h).Constructor;
                string? name = null;
                if (ctor.Kind == HandleKind.MemberReference)
                {
                    var parent = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                    if (parent.Kind == HandleKind.TypeReference) name = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
                }
                else if (ctor.Kind == HandleKind.MethodDefinition)
                {
                    name = md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType()).Name);
                }
                if (name != null && ObfuscatorMarkers.Contains(name) && !markers.Contains(name)) markers.Add(name);
            }
        }
        if (md.IsAssembly) ScanAttributes(md.GetAssemblyDefinition().GetCustomAttributes());
        ScanAttributes(md.GetModuleDefinition().GetCustomAttributes());

        double shortRatio = total == 0 ? 0 : (double)shortNames / total, oddRatio = total == 0 ? 0 : (double)odd / total;
        var evidence = new List<string>();
        if (markers.Count > 0) evidence.Add("carries obfuscator / protector marker attributes: " + string.Join(", ", markers));
        if (shortNames >= 20 && shortRatio >= 0.4) evidence.Add($"{shortRatio:P0} of its names ({shortNames} of {total}) are one or two letters, which compilers do not produce and developers rarely choose");
        if (odd >= 5 && oddRatio >= 0.1) evidence.Add($"{odd} of {total} names contain non-ASCII, control or invisible characters");

        return new
        {
            likely = evidence.Count > 0,
            evidence,
            namesChecked = total,
            shortNames,
            unusualNames = odd,
            examples,
            markers,
            // what this does and does not mean, so the UI never has to guess
            note = evidence.Count > 0
                ? "Signs of obfuscation were found. Names are shown exactly as they are in the file, so they will not mean much; packed or encrypted method bodies, reflection and generated code cannot be resolved, and the graphs may be incomplete."
                : "No signs of obfuscation were found in the names or attributes. That is not proof: an obfuscator that keeps readable names would not be detected."
        };
    }

    private static bool HasUnusualCharacters(string name)
    {
        foreach (char c in name)
        {
            if (c > 127) return true;                                               // look-alike letters, other scripts used as camouflage
            if (c < 32) return true;                                                // control characters
        }
        return false;
    }

    // Shows what the unusual characters are instead of printing something invisible.
    private static string Printable(string name) =>
        string.Concat(name.Select(c => c > 32 && c < 127 ? c.ToString() : "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    private static bool HasAttribute(MetadataReader md, string attributeName)
    {
        if (!md.IsAssembly) return false;
        foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var ctor = md.GetCustomAttribute(h).Constructor;
            if (ctor.Kind != HandleKind.MemberReference) continue;
            var parent = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
            if (parent.Kind == HandleKind.TypeReference && md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) == attributeName) return true;
        }
        return false;
    }

    private static bool HasDebugInfoDirectory(PEReader pe)
    {
        try { return pe.ReadDebugDirectory().Any(e => e.Type == DebugDirectoryEntryType.CodeView || e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb); }
        catch (BadImageFormatException) { return false; }
    }
}
