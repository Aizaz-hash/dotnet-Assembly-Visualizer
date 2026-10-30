using System.Text.Json.Nodes;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Cli;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    public class ExportTests
    {
        private static async Task<ScanReport> Report()
        {
            var scanner = new VulnerabilityScanner(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), true, null, Path.GetTempPath(), "x", new DependencyResolver(LevelFixtures.Graph(), _ => false));
            return await scanner.ScanAsync(new ScanRequest
            {
                IncludeApplication = false, RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0",
                References = LevelFixtures.Refs(("PkgA", "1.0.0.0"), ("Internal.Private", "1.0.0.0"))
            }, default);
        }

        [Fact]
        public async Task CycloneDx_lists_identified_packages_their_dependency_edges_and_the_vulnerabilities()
        {
            var bom = ReportExporters.CycloneDx(await Report(), "MyLib");

            Assert.Equal("CycloneDX", (string)bom["bomFormat"]!);
            Assert.Equal("1.5", (string)bom["specVersion"]!);
            var comps = bom["components"]!.AsArray().Select(c => (string)c!["purl"]!).ToList();
            Assert.Contains("pkg:nuget/PkgA@1.0.0", comps);
            Assert.Contains("pkg:nuget/PkgD@3.0.0", comps);
            Assert.DoesNotContain(comps, p => p.Contains("Internal.Private"));                      // never invented: it could not be resolved

            var deps = bom["dependencies"]!.AsArray().ToDictionary(d => (string)d!["ref"]!, d => d!["dependsOn"]!.AsArray().Select(x => (string)x!).ToList());
            Assert.Contains("pkg:nuget/PkgA@1.0.0", deps["root-component"]);                         // level 1 hangs off the root
            Assert.Contains("pkg:nuget/PkgB@1.1.0", deps["pkg:nuget/PkgA@1.0.0"]);                   // level 2 hangs off its parent, not the root
            Assert.DoesNotContain("pkg:nuget/PkgB@1.1.0", deps["root-component"]);
            Assert.Contains("pkg:nuget/PkgD@3.0.0", deps["pkg:nuget/PkgC@2.0.0"]);

            var vulns = bom["vulnerabilities"]!.AsArray();
            Assert.Equal(4, vulns.Count);
            var v = vulns.Single(x => ((string)x!["affects"]![0]!["ref"]!) == "pkg:nuget/PkgD@3.0.0")!;
            Assert.Equal("critical", (string)v["ratings"]![0]!["severity"]!);                      // CVSS 9.8 computed from the vector
            Assert.Equal("CVSSv31", (string)v["ratings"]![0]!["method"]!);
            Assert.StartsWith("urn:uuid:", (string)bom["serialNumber"]!);
        }

        [Fact]
        public async Task Sarif_has_one_rule_per_advisory_one_result_per_package_and_maps_severity_to_level()
        {
            var sarif = ReportExporters.Sarif(await Report(), "src\\MyLib.dll");

            Assert.Equal("2.1.0", (string)sarif["version"]!);
            var run = sarif["runs"]![0]!;
            Assert.Equal(4, run["results"]!.AsArray().Count);
            Assert.Equal(4, run["tool"]!["driver"]!["rules"]!.AsArray().Count);
            var first = run["results"]![0]!;
            Assert.Equal("error", (string)first["level"]!);                                           // Critical/High -> error
            Assert.Equal("src/MyLib.dll", (string)first["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"]!);
            Assert.Contains("dependency level", (string)first["message"]!["text"]!);
            Assert.False(string.IsNullOrEmpty((string)run["tool"]!["driver"]!["rules"]![0]!["properties"]!["security-severity"]!));
        }

        [Fact]
        public void An_empty_report_still_produces_valid_documents()
        {
            var empty = new ScanReport { Complete = true };
            Assert.Empty(ReportExporters.Sarif(empty, "a.dll")["runs"]![0]!["results"]!.AsArray());
            var bom = ReportExporters.CycloneDx(empty, "a");
            Assert.Empty(bom["components"]!.AsArray());
            Assert.Single(bom["dependencies"]!.AsArray());                                           // just the root
        }
    }

    public class CliScanTests : IDisposable
    {
        private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dav-cli-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

        private const string Manifest = """
        { "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
          "targets": { ".NETCoreApp,Version=v8.0": {
              "App/1.0": { "dependencies": { "PkgA": "1.0.0" } },
              "PkgA/1.0.0": { "dependencies": { "PkgB": "1.1.0" } },
              "PkgB/1.1.0": { "dependencies": { "PkgC": "2.0.0" } },
              "PkgC/2.0.0": { "dependencies": { "PkgD": "3.0.0" } },
              "PkgD/3.0.0": { } } },
          "libraries": { "App/1.0": { "type": "project" }, "PkgA/1.0.0": { "type": "package" }, "PkgB/1.1.0": { "type": "package" },
                         "PkgC/2.0.0": { "type": "package" }, "PkgD/3.0.0": { "type": "package" } } }
        """;

        private string Assembly()
        {
            var path = Path.Combine(_dir, "App.dll");
            File.Copy(typeof(CliScanTests).Assembly.Location, path, true);
            return path;
        }

        private string ManifestFile()
        {
            var path = Path.Combine(_dir, "manifest.json");
            File.WriteAllText(path, Manifest);
            return path;
        }

        private static async Task<(int Code, string Out, string Err)> Run(FakeOsvClient osv, params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            int code = await CliScan.RunAsync(args, osv, new FakeNuGetClient(), o, e);
            return (code, o.ToString(), e.ToString());
        }

        [Fact]
        public async Task Findings_at_or_above_the_threshold_fail_the_build_and_the_files_are_written()
        {
            var sarif = Path.Combine(_dir, "r.sarif"); var sbom = Path.Combine(_dir, "bom.json"); var json = Path.Combine(_dir, "r.json");
            var (code, output, _) = await Run(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), Assembly(), "--manifest", ManifestFile(), "--sarif", sarif, "--sbom", sbom, "--json", json);

            Assert.Equal(CliScan.FindingsAtOrAboveThreshold, code);
            Assert.Contains("FAIL: 4 finding(s) at or above 'high'", output);
            Assert.Contains("(level 4)", output);
            Assert.Equal("2.1.0", (string)JsonNode.Parse(File.ReadAllText(sarif))!["version"]!);
            Assert.Equal("CycloneDX", (string)JsonNode.Parse(File.ReadAllText(sbom))!["bomFormat"]!);
            Assert.NotNull(JsonNode.Parse(File.ReadAllText(json))!["findings"]);
        }

        [Theory]
        [InlineData("none", 0)]
        [InlineData("critical", 1)]       // the fixture advisories have a 9.8 vector: Critical
        [InlineData("high", 1)]
        [InlineData("medium", 1)]
        public async Task The_fail_on_threshold_decides_the_exit_code(string failOn, int expected)
        {
            var (code, _, _) = await Run(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), Assembly(), "--manifest", ManifestFile(), "--fail-on", failOn);
            Assert.Equal(expected, code);
        }

        [Fact]
        public async Task A_clean_scan_exits_zero()
        {
            var (code, output, _) = await Run(new FakeOsvClient(), Assembly(), "--manifest", ManifestFile());
            Assert.Equal(0, code);
            Assert.Contains("OK: 0 finding(s)", output);
        }

        [Fact]
        public async Task An_unreachable_database_is_never_reported_as_ok_unless_explicitly_allowed()
        {
            var (code, output, _) = await Run(new FakeOsvClient { Unreachable = true }, Assembly(), "--manifest", ManifestFile());
            Assert.Equal(CliScan.ScanIncomplete, code);
            Assert.Contains("INCOMPLETE", output);

            var (allowed, _, _) = await Run(new FakeOsvClient { Unreachable = true }, Assembly(), "--manifest", ManifestFile(), "--allow-incomplete");
            Assert.Equal(0, allowed);
        }

        [Fact]
        public async Task A_deps_json_next_to_the_assembly_is_used_automatically()
        {
            var asm = Assembly();
            File.WriteAllText(Path.Combine(_dir, "App.deps.json"), Manifest);
            var (code, output, _) = await Run(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), asm);
            Assert.Equal(1, code);
            Assert.Contains("exact versions from App.deps.json", output);
        }

        [Theory]
        [InlineData("")]
        [InlineData("missing.dll")]
        [InlineData("--bogus")]
        public async Task Bad_arguments_exit_two_with_usage(string args)
        {
            var (code, _, err) = await Run(new FakeOsvClient(), args.Length == 0 ? Array.Empty<string>() : new[] { args });
            Assert.Equal(CliScan.UsageOrInputError, code);
            Assert.Contains("Usage:", err);
        }

        [Fact]
        public async Task A_file_that_is_not_a_dotnet_assembly_and_bad_option_values_exit_two()
        {
            var text = Path.Combine(_dir, "notes.dll"); File.WriteAllText(text, "this is not a PE file");
            Assert.Equal(2, (await Run(new FakeOsvClient(), text)).Code);
            Assert.Equal(2, (await Run(new FakeOsvClient(), Assembly(), "--fail-on", "sometimes")).Code);
            Assert.Equal(2, (await Run(new FakeOsvClient(), Assembly(), "--max-level", "9")).Code);
            Assert.Equal(2, (await Run(new FakeOsvClient(), Assembly(), "--manifest", Path.Combine(_dir, "nope.json"))).Code);
        }
    }
    public class ExportApiTests : IClassFixture<ApiTests.AppFactory>
    {
        private readonly ApiTests.AppFactory _factory;
        public ExportApiTests(ApiTests.AppFactory factory) => _factory = factory;

        private static async Task<string> ReportJson()
        {
            var scanner = new VulnerabilityScanner(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), true, null, Path.GetTempPath(), "x", new DependencyResolver(LevelFixtures.Graph(), _ => false));
            var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = false, RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0", References = LevelFixtures.Refs(("PkgA", "1.0.0.0")) }, default);
            return System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }

        [Theory]
        [InlineData("cyclonedx", "application/vnd.cyclonedx+json", "bomFormat")]
        [InlineData("sarif", "application/sarif+json", "runs")]
        public async Task A_report_posted_by_the_UI_comes_back_as_the_requested_format(string format, string contentType, string marker)
        {
            using var client = _factory.CreateClient();
            var response = await client.PostAsync($"/api/vulnerabilities/export/{format}?assembly=MyLib.dll",
                new System.Net.Http.StringContent(await ReportJson(), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
            Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync())![marker]);
        }

        [Fact]
        public async Task An_unknown_format_is_a_404()
        {
            using var client = _factory.CreateClient();
            var response = await client.PostAsync("/api/vulnerabilities/export/pdf", new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
