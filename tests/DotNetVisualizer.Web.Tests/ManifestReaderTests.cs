using System.Text;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    public class DependencyManifestReaderTests
    {
        private static Stream S(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        // App -> A -> B -> C -> D (a chain), App -> Shared, A -> Shared, and an unreferenced runtime pack
        private const string DepsJson = """
        { "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
          "targets": { ".NETCoreApp,Version=v8.0": {
              "App/1.0.0": { "dependencies": { "A": "1.0.0", "Shared": "2.0.0" } },
              "A/1.0.0": { "dependencies": { "B": "1.1.0", "Shared": "2.0.0" }, "runtime": { "lib/a.dll": {} } },
              "B/1.1.0": { "dependencies": { "C": "2.0.0" }, "runtime": { "lib/b.dll": {} } },
              "C/2.0.0": { "dependencies": { "D": "3.0.0" }, "runtime": { "lib/c.dll": {} } },
              "D/3.0.0": { "dependencies": { "A": "1.0.0" }, "runtime": { "lib/d.dll": {} } },
              "Shared/2.0.0": { "runtime": { "lib/s.dll": {} } } } },
          "libraries": {
              "App/1.0.0": { "type": "project" },
              "A/1.0.0": { "type": "package" }, "B/1.1.0": { "type": "package" }, "C/2.0.0": { "type": "package" },
              "D/3.0.0": { "type": "package" }, "Shared/2.0.0": { "type": "package" } } }
        """;

        [Fact]
        public void Deps_json_gets_levels_from_the_project_downwards_with_cycles_and_shared_packages_handled()
        {
            var c = DependencyManifestReader.Read(S(DepsJson), "deps-file").ToDictionary(x => x.Name);
            Assert.Equal(new[] { 1, 2, 3, 4, 1 }, new[] { "A", "B", "C", "D", "Shared" }.Select(n => c[n].Level!.Value));   // Shared is direct AND required by A: level 1
            Assert.Equal("App → A → B → C → D", c["D"].Via);                  // the D -> A cycle adds nothing
            Assert.All(c.Values, x => Assert.Equal(("exact", "confirmed"), (x.VersionSource, x.Confidence)));
            Assert.Equal(true, c["A"].ShipsAssembly);
        }

        [Fact]
        public void Project_assets_json_uses_projectFileDependencyGroups_as_the_roots_and_includes_indirect_packages()
        {
            var json = """
            { "version": 3,
              "targets": { "net8.0": {
                  "Direct/1.0.0": { "dependencies": { "Indirect": "2.0.0" }, "compile": {}, "runtime": { "lib/x.dll": {} } },
                  "Indirect/2.0.0": { "dependencies": { "Deeper": "3.0.0" } },
                  "Deeper/3.0.0": { } } },
              "libraries": { "Direct/1.0.0": { "type": "package" }, "Indirect/2.0.0": { "type": "package" }, "Deeper/3.0.0": { "type": "package" },
                             "MyProj/1.0.0": { "type": "project" } },
              "projectFileDependencyGroups": { "net8.0": [ "Direct >= 1.0.0" ] } }
            """;
            var c = DependencyManifestReader.Read(S(json), "deps-file").ToDictionary(x => x.Name);
            Assert.Equal(new[] { 1, 2, 3 }, new[] { "Direct", "Indirect", "Deeper" }.Select(n => c[n].Level!.Value));
            Assert.Equal("MyProj → Direct → Indirect → Deeper", c["Deeper"].Via);
        }

        [Fact]
        public void Packages_lock_json_marks_direct_and_transitive_packages_and_skips_project_references()
        {
            var json = """
            { "version": 1, "dependencies": { "net8.0": {
                "Newtonsoft.Json": { "type": "Direct", "requested": "[12.0.1, )", "resolved": "12.0.1", "dependencies": { "Transitive.One": "1.0.0" } },
                "Transitive.One": { "type": "Transitive", "resolved": "1.0.0", "dependencies": { "Transitive.Two": "2.0.0" } },
                "Transitive.Two": { "type": "Transitive", "resolved": "2.0.0" },
                "Other.Project": { "type": "Project", "dependencies": { "X": "1.0.0" } } } } }
            """;
            var c = DependencyManifestReader.Read(S(json), "deps-file").ToDictionary(x => x.Name);
            Assert.Equal(3, c.Count);
            Assert.Equal(("12.0.1", 1), (c["Newtonsoft.Json"].Version, c["Newtonsoft.Json"].Level!.Value));
            Assert.Equal(3, c["Transitive.Two"].Level);
            Assert.Equal("project → Newtonsoft.Json → Transitive.One → Transitive.Two", c["Transitive.Two"].Via);
        }

        [Fact]
        public void A_manifest_without_a_graph_still_lists_every_package_with_no_level()
        {
            var json = """{ "libraries": { "X/1.0.0": { "type": "package" } } }""";
            var c = Assert.Single(DependencyManifestReader.Read(S(json), "deps-file"));
            Assert.Null(c.Level);
        }

        [Theory]
        [InlineData("[1,2]")]
        [InlineData("{ \"something\": 1 }")]
        [InlineData("{ \"dependencies\": { \"net8.0\": { } } }")]
        public void Files_that_are_not_manifests_are_rejected(string text) =>
            Assert.ThrowsAny<Exception>(() => DependencyManifestReader.Read(S(text), "deps-file"));
    }

    public class ManifestScanTests
    {
        [Fact]
        public async Task A_manifest_with_a_graph_replaces_the_estimates_and_gives_levels_to_every_finding()
        {
            var osv = LevelFixtures.OsvWithVulnerabilityAtEveryLevel();
            var nuget = LevelFixtures.Graph();
            var scanner = new VulnerabilityScanner(osv, true, null, Path.GetTempPath(), "x", new DependencyResolver(nuget, _ => false));
            var manifest = new[]
            {
                new DotNetVisualizer.Web.Models.ScanComponent("NuGet", "PkgA", "1.0.0", "deps-file", "confirmed", null, 1, "App → PkgA"),
                new DotNetVisualizer.Web.Models.ScanComponent("NuGet", "PkgB", "1.1.0", "deps-file", "confirmed", null, 2, "App → PkgA → PkgB"),
            };
            var report = await scanner.ScanAsync(new ScanRequest
            {
                IncludeApplication = false, RootName = "App", References = LevelFixtures.Refs(("PkgA", "1.0.0.0")), ExtraComponents = manifest.ToList()
            }, default);

            Assert.Equal(0, nuget.NuspecCalls);                               // no guessing beside an exact manifest
            Assert.Equal(new[] { ("PkgA", 1), ("PkgB", 2) }, report.Findings.OrderBy(f => f.Level).Select(f => (f.Component, f.Level!.Value)).ToArray());
            Assert.All(report.Findings, f => Assert.Equal("confirmed", f.Confidence));
            Assert.Equal(2, report.MaxLevelResolved);
        }
    }
}
