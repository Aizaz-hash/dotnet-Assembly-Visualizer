using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    public sealed class FakeNuGetClient : INuGetClient
    {
        public Dictionary<string, List<string>> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, NuspecInfo> Nuspecs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AllDown { get; set; }
        public int NuspecCalls;

        public Task<IReadOnlyList<string>?> GetVersionsAsync(string packageId, CancellationToken ct)
        {
            if (AllDown || Down.Contains(packageId)) throw new NuGetUnavailableException("down");
            return Task.FromResult<IReadOnlyList<string>?>(Versions.TryGetValue(packageId, out var v) ? v : null);
        }

        public Task<NuspecInfo?> GetNuspecAsync(string packageId, string version, CancellationToken ct)
        {
            Interlocked.Increment(ref NuspecCalls);
            if (AllDown || Down.Contains(packageId)) throw new NuGetUnavailableException("down");
            return Task.FromResult(Nuspecs.TryGetValue($"{packageId}@{version}", out var n) ? n : null);
        }

        public FakeNuGetClient Package(string id, string version, params (string dep, string range)[] deps)
        {
            if (!Versions.TryGetValue(id, out var list)) Versions[id] = list = new List<string>();
            if (!list.Contains(version)) list.Add(version);
            Nuspecs[$"{id}@{version}"] = new NuspecInfo(id, version, new[]
            {
                new NuspecDependencyGroup("netstandard2.0", deps.Select(d => new NuspecDependency(d.dep, d.range)).ToList())
            });
            return this;
        }
    }

    public static class LevelFixtures
    {
        /// <summary>
        /// MyLib (level 0) references PkgA, Shared, Internal.Private, System.Runtime and (optionally) others.
        ///   PkgA 1.0.0 -> PkgB [1.1.0,)  and  Shared [2.0.0]
        ///   PkgB 1.1.0 -> PkgC 2.0.0
        ///   PkgC 2.0.0 -> PkgD [3.0.0]  and  PkgA 1.0.0 (a cycle back to level 1)
        ///   PkgD 3.0.0 -> PkgE 1.0.0   (level 5: must not be expanded)
        /// There is a vulnerability in PkgA (L1), PkgB (L2), PkgC (L3) and PkgD (L4).
        /// </summary>
        public static FakeNuGetClient Graph()
        {
            var nuget = new FakeNuGetClient();
            nuget.Package("PkgA", "1.0.0", ("PkgB", "[1.1.0, )"), ("Shared", "[2.0.0]"));
            nuget.Package("PkgB", "1.1.0", ("PkgC", "2.0.0"));
            nuget.Package("PkgC", "2.0.0", ("PkgD", "[3.0.0]"), ("PkgA", "1.0.0"));
            nuget.Package("PkgD", "3.0.0", ("PkgE", "1.0.0"));
            nuget.Package("PkgE", "1.0.0");
            nuget.Package("Shared", "2.0.0");
            return nuget;
        }

        public static List<ReferencedAssembly> Refs(params (string name, string version)[] r) =>
            r.Select(x => new ReferencedAssembly { Name = x.name, Version = x.version }).ToList();

        public static FakeOsvClient OsvWithVulnerabilityAtEveryLevel()
        {
            var osv = new FakeOsvClient();
            foreach (var (pkg, id) in new[] { ("PkgA", "GHSA-aaaa-aaaa-aaaa"), ("PkgB", "GHSA-bbbb-bbbb-bbbb"), ("PkgC", "GHSA-cccc-cccc-cccc"), ("PkgD", "GHSA-dddd-dddd-dddd") })
            {
                var version = pkg switch { "PkgA" => "1.0.0", "PkgB" => "1.1.0", "PkgC" => "2.0.0", _ => "3.0.0" };
                osv.Matches[$"{pkg}@{version}"] = new List<string> { id };
                osv.Records[id] = $$"""
                    { "id": "{{id}}", "summary": "flaw in {{pkg}}", "aliases": ["CVE-2099-{{id.Substring(5, 4)}}"],
                      "database_specific": { "severity": "HIGH" },
                      "severity": [ { "type": "CVSS_V3", "score": "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H" } ],
                      "affected": [ { "package": { "name": "{{pkg}}", "ecosystem": "NuGet" },
                          "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "9.9.9" } ] } ] } ] }
                    """;
            }
            return osv;
        }
    }
}
