using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Unit
{
    /// <summary>Scriptable stand-in for NuGet.org: a published-versions index plus .nuspec dependency groups.</summary>


    public class NuGetVersioningTests
    {
        [Theory]
        [InlineData("1.2.3", "1.2.3")]
        [InlineData("[1.2.3, )", "1.2.3")]
        [InlineData("(1.2.3, 2.0.0)", "1.2.3")]
        [InlineData("[1.2.3]", "1.2.3")]
        [InlineData("[1.0, 2.0)", "1.0")]
        [InlineData("(, 2.0]", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Minimum_version_of_a_range(string? range, string? expected) =>
            Assert.Equal(expected, NuGetVersioning.MinimumVersion(range));

        [Fact]
        public void Estimates_the_package_version_from_an_assembly_version()
        {
            var newtonsoft = new[] { "11.0.1", "12.0.1", "12.0.2", "12.0.3", "13.0.1", "13.0.3-beta1" };
            Assert.Equal("12.0.1", NuGetVersioning.EstimatePackageVersion(newtonsoft, "12.0.0.0"));
            Assert.Equal("2.0.8", NuGetVersioning.EstimatePackageVersion(new[] { "2.0.5", "2.0.8", "2.0.9", "2.0.10" }, "2.0.8.0"));
            Assert.Equal("4.1.0", NuGetVersioning.EstimatePackageVersion(new[] { "4.1.0", "4.2.0" }, "4.0.0.0"));      // falls back to the major
            Assert.Null(NuGetVersioning.EstimatePackageVersion(new[] { "1.0.0" }, "9.0.0.0"));
            Assert.Null(NuGetVersioning.EstimatePackageVersion(new[] { "1.0.0" }, "not-a-version"));
        }

        [Theory]
        [InlineData("net8.0", "net", 8)]
        [InlineData("netstandard2.0", "netstandard", 2)]
        [InlineData(".NETStandard,Version=v2.1", "netstandard", 2)]
        [InlineData(".NETCoreApp,Version=v8.0", "net", 8)]
        [InlineData(".NETCoreApp,Version=v3.1", "netcoreapp", 3)]
        [InlineData("net462", "netfx", 4)]
        [InlineData(".NETFramework,Version=v4.8", "netfx", 4)]
        public void Parses_target_frameworks(string input, string family, int major)
        {
            var tfm = NuGetVersioning.ParseTfm(input);
            Assert.NotNull(tfm);
            Assert.Equal((family, major), (tfm!.Family, tfm.Version.Major));
        }

        [Fact]
        public void Picks_the_dependency_group_restore_would_use()
        {
            NuspecDependencyGroup G(string? tfm, string dep) => new(tfm, new[] { new NuspecDependency(dep, null) });
            var groups = new[] { G("net462", "fx"), G("netstandard2.0", "std20"), G("netstandard2.1", "std21"), G("net6.0", "net6") };

            Assert.Equal("net6", NuGetVersioning.SelectGroup(groups, ".NETCoreApp,Version=v8.0")!.Dependencies[0].Id);
            Assert.Equal("std20", NuGetVersioning.SelectGroup(groups, ".NETStandard,Version=v2.0")!.Dependencies[0].Id);
            Assert.Equal("fx", NuGetVersioning.SelectGroup(groups, ".NETFramework,Version=v4.8")!.Dependencies[0].Id);   // net462 is the only netfx group
            Assert.Equal("net6", NuGetVersioning.SelectGroup(groups, null)!.Dependencies[0].Id);                          // unknown framework: the newest group
            Assert.Null(NuGetVersioning.SelectGroup(Array.Empty<NuspecDependencyGroup>(), "net8.0"));
        }

        [Fact]
        public void Parses_a_nuspec_with_framework_groups_and_without_a_namespace_assumption()
        {
            var info = NuspecParser.Parse("""
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
                  <id>Demo</id><version>1.2.3</version>
                  <dependencies>
                    <group targetFramework=".NETStandard2.0"><dependency id="A" version="1.0.0" /><dependency id="B" version="[2.0.0, )" /></group>
                    <group targetFramework="net6.0" />
                  </dependencies></metadata></package>
                """, "x", "0");
            Assert.Equal("Demo", info.Id);
            Assert.Equal(2, info.Groups.Count);
            Assert.Equal(new[] { "A", "B" }, info.Groups[0].Dependencies.Select(d => d.Id));
            Assert.Empty(info.Groups[1].Dependencies);
        }
    }

    public class DependencyResolverTests
    {
        private static DependencyResolver Resolver(FakeNuGetClient nuget) => new(nuget, name => name == "System.Collections");

        private static DependencyRequest Request(params (string, string)[] refs) => new()
        {
            RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0", References = LevelFixtures.Refs(refs)
        };

        [Fact]
        public async Task Walks_dependencies_to_level_4_and_stops()
        {
            var r = await Resolver(LevelFixtures.Graph()).ResolveAsync(Request(("PkgA", "1.0.0.0")), default);

            Assert.Equal(new[] { ("PkgA", 1), ("PkgB", 2), ("PkgC", 3), ("PkgD", 4) },
                r.Nodes.OrderBy(n => n.Level).Select(n => (n.Name, n.Level)).Where(x => x.Name != "Shared").ToArray());
            Assert.Equal(4, r.MaxLevelResolved);
            Assert.DoesNotContain(r.Nodes, n => n.Name == "PkgE");                 // level 5 is not expanded
            Assert.Contains(r.Notes, n => n.Contains("level 4") && n.Contains("not expanded"));
        }

        [Fact]
        public async Task Level_reflects_the_shortest_path_and_each_package_appears_once()
        {
            var nuget = LevelFixtures.Graph();
            var r = await Resolver(nuget).ResolveAsync(Request(("PkgA", "1.0.0.0"), ("Shared", "2.0.0.0")), default);

            var shared = Assert.Single(r.Nodes, n => n.Name == "Shared");
            Assert.Equal(1, shared.Level);                 // referenced directly AND required by PkgA (level 2): keeps level 1
            Assert.Equal(1, shared.AlsoRequiredBy);
            Assert.Single(r.Nodes, n => n.Name == "PkgA"); // the cycle PkgC -> PkgA does not add a second PkgA
            Assert.Equal(r.Nodes.Count, r.Nodes.Select(n => n.Name + "@" + n.Version).Distinct().Count());
        }

        [Fact]
        public async Task Circular_package_dependencies_terminate()
        {
            var nuget = new FakeNuGetClient()
                .Package("X", "1.0.0", ("Y", "1.0.0")).Package("Y", "1.0.0", ("X", "1.0.0"));
            var r = await Resolver(nuget).ResolveAsync(Request(("X", "1.0.0.0")), default);
            Assert.Equal(new[] { "X", "Y" }, r.Nodes.Select(n => n.Name).OrderBy(n => n));
            Assert.Equal(2, nuget.NuspecCalls);            // each package's metadata is read once
        }

        [Fact]
        public async Task Max_level_is_respected()
        {
            var r = await Resolver(LevelFixtures.Graph()).ResolveAsync(
                new DependencyRequest { RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0", References = LevelFixtures.Refs(("PkgA", "1.0.0.0")), MaxLevel = 2 }, default);
            Assert.Equal(2, r.Nodes.Max(n => n.Level));
            Assert.DoesNotContain(r.Nodes, n => n.Name == "PkgC");
        }

        [Fact]
        public async Task Missing_metadata_is_kept_and_labelled_not_dropped()
        {
            var nuget = LevelFixtures.Graph();
            nuget.Package("NoLower", "1.0.0");
            nuget.Package("HasOpenRange", "1.0.0", ("Capped", "(, 2.0]"));
            var r = await Resolver(nuget).ResolveAsync(Request(
                ("Internal.Private", "1.0.0.0"),       // not on NuGet
                ("System.Runtime", "8.0.0.0"),         // supplied by the runtime
                ("System.Collections", "8.0.0.0"),     // in the runtime's assembly list, but this is a netstandard library
                ("NoLower", "99.0.0.0"),               // exists, but no version matches the assembly version
                ("HasOpenRange", "1.0.0.0")), default);

            var priv = Assert.Single(r.Nodes, n => n.Name == "Internal.Private");
            Assert.Equal(("unresolved", false, false), (priv.Resolution, priv.PackageIdentified, priv.VersionIdentified));

            var fw = Assert.Single(r.Nodes, n => n.Name == "System.Runtime");
            Assert.Equal("framework", fw.Resolution);

            var noVersion = Assert.Single(r.Nodes, n => n.Name == "NoLower");
            Assert.Equal(("unresolved", true, false), (noVersion.Resolution, noVersion.PackageIdentified, noVersion.VersionIdentified));

            var capped = Assert.Single(r.Nodes, n => n.Name == "Capped");
            Assert.Equal(("unresolved", false), (capped.Resolution, capped.VersionIdentified));
            Assert.Equal(2, capped.Level);
        }

        [Fact]
        public async Task Runtime_assemblies_are_framework_for_modern_dotnet_but_packages_for_netstandard()
        {
            var nuget = new FakeNuGetClient().Package("System.Collections", "4.3.0");
            var modern = await Resolver(nuget).ResolveAsync(new DependencyRequest { References = LevelFixtures.Refs(("System.Collections", "4.3.0.0")), TargetFramework = ".NETCoreApp,Version=v8.0" }, default);
            Assert.Equal("framework", modern.Nodes.Single().Resolution);

            var std = await Resolver(nuget).ResolveAsync(new DependencyRequest { References = LevelFixtures.Refs(("System.Collections", "4.3.0.0")), TargetFramework = ".NETStandard,Version=v2.0" }, default);
            Assert.Equal(("resolved", "4.3.0"), (std.Nodes.Single().Resolution, std.Nodes.Single().Version));
        }

        [Fact]
        public async Task A_manifest_version_wins_over_the_estimate()
        {
            var nuget = LevelFixtures.Graph();
            var r = await Resolver(nuget).ResolveAsync(new DependencyRequest
            {
                RootName = "MyLib", References = LevelFixtures.Refs(("PkgA", "1.0.0.0")),
                ExactVersions = new Dictionary<string, string> { ["PkgA"] = "1.0.0" }
            }, default);
            var a = r.Nodes.Single(n => n.Name == "PkgA");
            Assert.Equal(("1.0.0", "exact"), (a.Version, a.VersionSource));
        }

        [Fact]
        public async Task An_unreachable_nuget_is_reported_and_never_looks_like_no_dependencies()
        {
            var nuget = LevelFixtures.Graph();
            nuget.AllDown = true;
            var r = await Resolver(nuget).ResolveAsync(Request(("PkgA", "1.0.0.0")), default);
            Assert.Equal("lookup-unavailable", Assert.Single(r.Nodes).Resolution);
            Assert.Contains(r.Notes, n => n.Contains("could not be reached"));
        }

        [Fact]
        public async Task A_partial_outage_keeps_the_levels_that_were_read()
        {
            var nuget = LevelFixtures.Graph();
            nuget.Down.Add("PkgC");                         // PkgC's metadata cannot be read
            var r = await Resolver(nuget).ResolveAsync(Request(("PkgA", "1.0.0.0")), default);
            Assert.Contains(r.Nodes, n => n.Name == "PkgC" && n.Level == 3);
            Assert.DoesNotContain(r.Nodes, n => n.Name == "PkgD");
            Assert.Contains(r.Notes, n => n.Contains("PkgC") && n.Contains("could not be read"));
        }

        [Fact]
        public async Task Empty_and_duplicate_references_are_handled()
        {
            var empty = await Resolver(new FakeNuGetClient()).ResolveAsync(Request(), default);
            Assert.Empty(empty.Nodes);

            var nuget = new FakeNuGetClient().Package("PkgE", "1.0.0");
            var dup = await Resolver(nuget).ResolveAsync(Request(("PkgE", "1.0.0.0"), ("pkge", "1.0.0.0"), ("", "1.0.0.0")), default);
            Assert.Single(dup.Nodes);
        }
    }

    public class DependencyScanTests
    {
        private static VulnerabilityScanner Scanner(FakeOsvClient osv, FakeNuGetClient nuget) =>
            new(osv, true, null, Path.GetTempPath(), "no-such-app", new DependencyResolver(nuget, _ => false));

        private static ScanRequest Request(params (string, string)[] refs) => new()
        {
            IncludeApplication = false,                    // no manifest of any kind: only the loaded assembly's own references
            RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0", References = LevelFixtures.Refs(refs)
        };

        [Fact]
        public async Task Finds_a_vulnerability_at_each_of_levels_1_2_3_and_4_without_any_package_json_or_deps_file()
        {
            var report = await Scanner(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), LevelFixtures.Graph()).ScanAsync(Request(("PkgA", "1.0.0.0")), default);

            Assert.Equal(new[] { ("PkgA", 1), ("PkgB", 2), ("PkgC", 3), ("PkgD", 4) },
                report.Findings.OrderBy(f => f.Level).Select(f => (f.Component, f.Level!.Value)).ToArray());
            Assert.Equal(4, report.Summary.High + report.Summary.Critical);
            Assert.Equal(4, report.MaxLevelResolved);

            var deep = report.Findings.Single(f => f.Component == "PkgD");
            Assert.Equal("MyLib → PkgA → PkgB → PkgC → PkgD", deep.DependencyPath);
            Assert.Equal("potential", deep.Confidence);                                     // versions come from nuspec minimums, not a manifest
            Assert.Contains("lowest version the parent package accepts", deep.ConfidenceNote);
        }

        [Fact]
        public async Task Every_dependency_has_a_level_and_a_plain_status()
        {
            var nuget = LevelFixtures.Graph();
            var report = await Scanner(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), nuget).ScanAsync(Request(
                ("PkgA", "1.0.0.0"), ("Internal.Private", "1.0.0.0"), ("System.Runtime", "8.0.0.0")), default);

            Assert.Equal("Vulnerability found", report.Components.Single(c => c.Name == "PkgA").Status);
            Assert.Equal("Version identified", report.Components.Single(c => c.Name == "Shared").Status);        // checked, nothing found
            Assert.Equal("Dependency could not be resolved", report.Components.Single(c => c.Name == "Internal.Private").Status);
            Assert.StartsWith("Assembly discovered", report.Components.Single(c => c.Name == "System.Runtime").Status);
            Assert.All(report.Components, c => Assert.NotNull(c.Level));
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 3, 4 }.OrderBy(x => x), report.Components.Select(c => c.Level!.Value).OrderBy(x => x));
        }

        [Fact]
        public async Task A_database_outage_is_reported_as_lookup_unavailable_not_as_clean()
        {
            var osv = LevelFixtures.OsvWithVulnerabilityAtEveryLevel();
            osv.Unreachable = true;
            var report = await Scanner(osv, LevelFixtures.Graph()).ScanAsync(Request(("PkgA", "1.0.0.0")), default);

            Assert.False(report.Complete);
            Assert.Empty(report.Findings);
            Assert.All(report.Components, c => Assert.Equal("Vulnerability lookup unavailable", c.Status));
        }

        [Fact]
        public async Task An_unreachable_nuget_marks_the_dependency_unresolved_and_the_report_incomplete()
        {
            var nuget = LevelFixtures.Graph();
            nuget.AllDown = true;
            var report = await Scanner(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), nuget).ScanAsync(Request(("PkgA", "1.0.0.0")), default);

            Assert.False(report.Complete);
            var a = Assert.Single(report.Components);
            Assert.Equal("Dependency could not be resolved (NuGet unreachable)", a.Status);
            Assert.Contains(report.Notes, n => n.Contains("must not be read as \"no vulnerabilities\""));
        }

        [Fact]
        public async Task The_package_version_is_estimated_from_the_assembly_version_and_labelled_as_such()
        {
            var nuget = new FakeNuGetClient();
            foreach (var v in new[] { "12.0.1", "12.0.2", "13.0.1" }) nuget.Package("Newtonsoft.Json", v);
            var osv = new FakeOsvClient();
            osv.Matches["Newtonsoft.Json@12.0.1"] = new List<string> { "GHSA-aaaa-aaaa-aaaa" };
            osv.Records["GHSA-aaaa-aaaa-aaaa"] = """
                { "id": "GHSA-aaaa-aaaa-aaaa", "summary": "x", "database_specific": { "severity": "HIGH" },
                  "affected": [ { "package": { "name": "Newtonsoft.Json", "ecosystem": "NuGet" }, "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "13.0.1" } ] } ] } ] }
                """;

            var report = await Scanner(osv, nuget).ScanAsync(Request(("Newtonsoft.Json", "12.0.0.0")), default);

            var f = Assert.Single(report.Findings);
            Assert.Equal("12.0.1", f.InstalledVersion);
            Assert.Equal("potential", f.Confidence);
            Assert.Contains("matches the referenced assembly version 12.0.0.0", f.ConfidenceNote);
            Assert.Equal("assembly-estimate", report.Components.Single().VersionSource);
        }

        [Fact]
        public async Task A_deps_json_gives_exact_versions_that_replace_the_estimate()
        {
            var nuget = LevelFixtures.Graph();
            nuget.Package("PkgA", "1.0.5");                                       // a newer published version exists
            var osv = LevelFixtures.OsvWithVulnerabilityAtEveryLevel();
            var request = new ScanRequest
            {
                IncludeApplication = false, RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0",
                References = LevelFixtures.Refs(("PkgA", "1.0.0.0")),
                ExtraComponents = new List<ScanComponent> { new("NuGet", "PkgA", "1.0.0", "deps-file") }
            };
            var report = await Scanner(osv, nuget).ScanAsync(request, default);

            var a = report.Components.Single(c => c.Name == "PkgA" && c.Version == "1.0.0");
            Assert.Equal(("exact", 1), (a.VersionSource, a.Level));              // one entry, with its level, from the manifest version
            Assert.Equal("confirmed", report.Findings.Single(f => f.Component == "PkgA").Confidence);
        }

        [Fact]
        public async Task Nothing_resolvable_gives_an_explanation_instead_of_a_silent_empty_report()
        {
            var report = await Scanner(new FakeOsvClient(), new FakeNuGetClient()).ScanAsync(Request(("Internal.Private", "1.0.0.0")), default);
            Assert.False(report.Complete);
            Assert.Contains(report.Notes, n => n.Contains("No dependency could be checked") && n.Contains("could not be matched to a NuGet package"));
            Assert.True(report.NothingToCheck);
            Assert.Equal("Dependency could not be resolved", Assert.Single(report.Components).Status);
        }

        [Fact]
        public async Task An_assembly_that_only_references_the_runtime_is_complete_and_says_why_nothing_was_checked()
        {
            var osv = new FakeOsvClient();
            var scanner = new VulnerabilityScanner(osv, true, null, Path.GetTempPath(), "x", new DependencyResolver(new FakeNuGetClient(), name => name.StartsWith("Microsoft.") || name.StartsWith("System.")));
            var report = await scanner.ScanAsync(new ScanRequest
            {
                IncludeApplication = false, RootName = "MyTool", TargetFramework = ".NETCoreApp,Version=v8.0",
                References = LevelFixtures.Refs(("System.Linq", "8.0.0.0"), ("Microsoft.AspNetCore.Http", "8.0.0.0"), ("System.Runtime", "8.0.0.0"))
            }, default);

            Assert.True(report.NothingToCheck);
            Assert.True(report.Complete);                                  // nothing is missing: there simply is no package to check
            Assert.False(report.DatabaseReachable);                         // the database was never asked - the UI must not call that "unreachable"
            Assert.Empty(osv.Queried);
            Assert.Contains(report.Notes, n => n.Contains("All 3 assemblies it references are supplied by the .NET runtime"));
            Assert.All(report.Components, c => Assert.StartsWith("Assembly discovered", c.Status));
        }

        [Fact]
        public async Task A_mix_of_runtime_private_and_unreachable_dependencies_is_explained_and_incomplete()
        {
            var nuget = new FakeNuGetClient();
            nuget.Down.Add("Flaky.Pkg");
            nuget.Package("Other", "1.0.0");
            var scanner = new VulnerabilityScanner(new FakeOsvClient(), true, null, Path.GetTempPath(), "x", new DependencyResolver(nuget, name => name.StartsWith("System.")));
            var report = await scanner.ScanAsync(new ScanRequest
            {
                IncludeApplication = false, RootName = "MyTool", TargetFramework = ".NETCoreApp,Version=v8.0",
                References = LevelFixtures.Refs(("System.Linq", "8.0.0.0"), ("Private.Thing", "1.0.0.0"), ("Flaky.Pkg", "1.0.0.0"))
            }, default);

            Assert.True(report.NothingToCheck);
            Assert.False(report.Complete);
            var note = Assert.Single(report.Notes, n => n.StartsWith("No dependency could be checked"));
            Assert.Contains("1 supplied by the .NET runtime", note);
            Assert.Contains("1 could not be matched", note);
            Assert.Contains("1 could not be looked up because NuGet was unreachable", note);
        }

        [Fact]
        public async Task Disabled_scanning_does_not_call_nuget()
        {
            var nuget = LevelFixtures.Graph();
            var scanner = new VulnerabilityScanner(new FakeOsvClient(), false, null, Path.GetTempPath(), "x", new DependencyResolver(nuget, _ => false));
            var report = await scanner.ScanAsync(Request(("PkgA", "1.0.0.0")), default);
            Assert.Equal(0, nuget.NuspecCalls);
            Assert.False(report.Complete);
        }
    }

    public class PerformanceInstrumentationTests
    {
        [Fact]
        public void The_pipeline_timer_records_each_stage_in_order_with_real_durations()
        {
            var t = new PipelineTimer();
            Thread.Sleep(15);
            t.Mark("first");
            t.Mark("second");

            Assert.Equal(new[] { "first", "second" }, t.Stages.Select(s => s.Stage));
            Assert.InRange(t.Stages[0].Ms, 10, 2000);
            Assert.True(t.Stages[1].Ms < t.Stages[0].Ms);
            Assert.True(t.TotalMs >= t.Stages.Sum(s => s.Ms) - 0.5);
        }

        [Fact]
        public void The_analyzer_reports_a_timing_for_every_stage_of_a_real_assembly()
        {
            var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());
            using var stream = File.OpenRead(typeof(AssemblyAnalyzerService).Assembly.Location);

            dynamic result = analyzer.Analyze(stream);
            var json = System.Text.Json.JsonSerializer.Serialize((object)result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var stages = doc.RootElement.GetProperty("timings").GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("stage").GetString()).ToList();
            Assert.Equal(new[] { "load PE image", "read metadata tables", "lambda name scan (IL)", "type + method discovery", "call analysis (IL scan)", "graph construction", "cycle detection", "assembly references" }, stages);
            Assert.True(doc.RootElement.GetProperty("timings").GetProperty("totalMs").GetDouble() > 0);
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("assemblyName").GetString()));
            Assert.StartsWith(".NETCoreApp", doc.RootElement.GetProperty("targetFramework").GetString());
        }

        [Fact]
        public void Call_analysis_resolves_each_callee_once_so_cost_does_not_grow_with_the_number_of_call_sites()
        {
            // A regression guard for the measured bottleneck: analysing a large assembly (this one references hundreds of
            // framework members many times over) must stay far below the multi-second cost the per-call-site signature
            // decoding used to have.
            var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var stream = File.OpenRead(typeof(System.Text.Json.JsonSerializer).Assembly.Location)) analyzer.Analyze(stream);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"analysis took {sw.Elapsed.TotalSeconds:0.0}s");
        }
    }
}
