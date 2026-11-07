using System.Reflection.PortableExecutable;
using DotNetVisualizer.Core.ApiDiff;

namespace DotNetVisualizer.Core.Checks;

/// <summary>The result of comparing two builds of an assembly.</summary>
public sealed class VersionComparison
{
    public string OldName { get; init; } = "";
    public string OldVersion { get; init; } = "";
    public string NewName { get; init; } = "";
    public string NewVersion { get; init; } = "";

    public int Breaking { get; init; }
    public int Compatible { get; init; }
    public int Info { get; init; }
    public IReadOnlyList<ApiChange> Changes { get; init; } = Array.Empty<ApiChange>();

    /// <summary>What a semantic-versioning reading of the API changes requires: major, minor or patch.</summary>
    public string RequiredBump { get; init; } = "patch";
    /// <summary>What the version numbers actually did: major, minor, patch (build / revision) or none (same or lower).</summary>
    public string ActualBump { get; init; } = "none";
    /// <summary>True when the version number is too small for the changes (for example a breaking change without a major bump).</summary>
    public bool VersionMismatch { get; init; }
    /// <summary>The risk of the mismatch: High when a major bump is missing, Medium when a minor bump is missing, null when there is no mismatch.</summary>
    public RiskLevel? MismatchLevel { get; init; }

    public string OldFileHash { get; init; } = "";
    public string NewFileHash { get; init; } = "";
    public string OldApiFingerprint { get; init; } = "";
    public string NewApiFingerprint { get; init; } = "";
    /// <summary>identical file | same public API, code changed | public API changed</summary>
    public string Verdict { get; init; } = "";

    public IReadOnlyList<string> ChangedMethods { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AddedMethods { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedMethods { get; init; } = Array.Empty<string>();

    public string Summary => $"{OldName} {OldVersion} -> {NewName} {NewVersion}: {Verdict}; {Breaking} breaking, {Compatible} compatible, {Info} info; " +
                             $"required bump: {RequiredBump}, actual: {ActualBump}" + (VersionMismatch ? " (VERSION MISMATCH)" : "");
}

public static class AssemblyVersionComparison
{
    /// <summary>Compares two builds. Only metadata and IL are read; nothing is run.</summary>
    public static VersionComparison Compare(string oldPath, string newPath)
    {
        var oldApi = ReadApi(oldPath);
        var newApi = ReadApi(newPath);
        var diff = ApiDiffer.Compare(oldApi, newApi);

        string oldHash = Fingerprints.OfFile(oldPath), newHash = Fingerprints.OfFile(newPath);
        string oldFp = Fingerprints.OfApi(oldApi), newFp = Fingerprints.OfApi(newApi);

        var oldBodies = AssemblyFile.ReadGraph(oldPath, hashBodies: true).MethodBodyHashes;
        var newBodies = AssemblyFile.ReadGraph(newPath, hashBodies: true).MethodBodyHashes;
        var changed = newBodies.Where(kv => oldBodies.TryGetValue(kv.Key, out var h) && h != kv.Value).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var added = newBodies.Keys.Where(k => !oldBodies.ContainsKey(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var removed = oldBodies.Keys.Where(k => !newBodies.ContainsKey(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        string actual = ActualBump(oldApi.Version, newApi.Version);
        bool mismatch = Rank(diff.SuggestedBump) > Rank(actual) && diff.SuggestedBump != "patch";
        RiskLevel? level = !mismatch ? null : diff.SuggestedBump == "major" ? RiskLevel.High : RiskLevel.Medium;

        string verdict = oldHash == newHash ? "identical file"
            : oldFp == newFp ? "same public API, code changed"
            : "public API changed";

        return new VersionComparison
        {
            OldName = diff.OldName, OldVersion = diff.OldVersion, NewName = diff.NewName, NewVersion = diff.NewVersion,
            Breaking = diff.Breaking, Compatible = diff.Compatible, Info = diff.Info, Changes = diff.Changes,
            RequiredBump = diff.SuggestedBump, ActualBump = actual, VersionMismatch = mismatch, MismatchLevel = level,
            OldFileHash = oldHash, NewFileHash = newHash, OldApiFingerprint = oldFp, NewApiFingerprint = newFp, Verdict = verdict,
            ChangedMethods = changed, AddedMethods = added, RemovedMethods = removed
        };
    }

    private static ApiSurface ReadApi(string path)
    {
        using var stream = AssemblyFile.Open(path);
        return ApiSurfaceReader.Read(stream);
    }

    private static int Rank(string bump) => bump switch { "major" => 3, "minor" => 2, "patch" => 1, _ => 0 };

    /// <summary>The largest part of the version that went up; "none" when the new version is the same or lower.</summary>
    public static string ActualBump(string oldVersion, string newVersion)
    {
        if (!Version.TryParse(Normalize(oldVersion), out var o) || !Version.TryParse(Normalize(newVersion), out var n)) return "none";
        if (n.Major != o.Major) return n.Major > o.Major ? "major" : "none";
        if (n.Minor != o.Minor) return n.Minor > o.Minor ? "minor" : "none";
        if (n.Build != o.Build || n.Revision != o.Revision) return (n.Build > o.Build || (n.Build == o.Build && n.Revision > o.Revision)) ? "patch" : "none";
        return "none";
    }

    private static string Normalize(string v) => string.IsNullOrWhiteSpace(v) ? "0.0" : v.Contains('.') ? v : v + ".0";
}
