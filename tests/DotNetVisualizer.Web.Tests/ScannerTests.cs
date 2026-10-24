using System.Net;
using System.Text;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    public class DepsJsonReaderTests
    {
        private static Stream Stream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        [Fact]
        public void Reads_packages_and_skips_projects()
        {
            var components = DepsJsonReader.Read(Stream("""
                { "libraries": {
                    "MyApp/1.0.0": { "type": "project" },
                    "Newtonsoft.Json/12.0.1": { "type": "package" },
                    "System.Text.Json/8.0.0-rc.1": { "type": "package" }
                } }
                """), "deps-file");

            Assert.Equal(2, components.Count);
            Assert.Contains(components, c => c is { Name: "Newtonsoft.Json", Version: "12.0.1", Ecosystem: "NuGet", Confidence: "confirmed" });
            Assert.Contains(components, c => c.Name == "System.Text.Json" && c.Version == "8.0.0-rc.1");
        }

        [Fact]
        public void Rejects_files_that_are_not_dependency_manifests() =>
            Assert.Throws<InvalidDataException>(() => DepsJsonReader.Read(Stream("""{ "something": 1 }"""), "deps-file"));

        [Fact]
        public void Rejects_invalid_json() =>
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => DepsJsonReader.Read(Stream("not json"), "deps-file"));

        [Fact]
        public void Reads_the_bundled_script_version_from_its_banner()
        {
            var c = BundledScriptReader.ReadBanner("vis-network", "/**\n * vis-network\n *\n * @version 10.1.2\n * @date 2026-08-19\n */");
            Assert.NotNull(c);
            Assert.Equal(("npm", "vis-network", "10.1.2"), (c!.Ecosystem, c.Name, c.Version));
            Assert.Null(BundledScriptReader.ReadBanner("x", "no version here"));
        }
    }

    public class VulnerabilityScannerTests
    {
        private static ScanComponent Pkg(string name, string version) => new("NuGet", name, version, "deps-file");

        private static VulnerabilityScanner Scanner(FakeOsvClient osv, bool enabled = true, string? appDir = null) =>
            new(osv, enabled, null, appDir ?? Path.GetTempPath(), "no-such-app");

        private static ScanRequest Request(params ScanComponent[] components) =>
            new() { IncludeApplication = false, ExtraComponents = components.ToList() };

        [Fact]
        public async Task Reports_verified_findings_with_computed_severity_and_remediation()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-2cwj-8chv-9pp9" };
            osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            Assert.True(report.Complete);
            Assert.True(report.DatabaseReachable);
            var f = Assert.Single(report.Findings);
            Assert.Equal("CVE-2018-1285", f.Id);                       // CVE preferred over the database id
            Assert.Equal("GHSA-2cwj-8chv-9pp9", f.DatabaseId);
            Assert.Equal("Critical", f.Severity);
            Assert.Equal(9.8, f.CvssScore);
            Assert.Equal("confirmed", f.Confidence);
            Assert.Equal("2.0.10", f.RecommendedVersion);
            Assert.Equal("Open", f.Status);
            Assert.Contains(f.Remediation, r => r.Contains("dotnet add package log4net --version 2.0.10"));
            Assert.Contains("CWE-611", f.Cwes);
            Assert.Equal(1, report.Summary.Critical);
            Assert.Equal(1, report.Components.Single().Findings);
        }

        [Fact]
        public async Task Orders_findings_by_severity_then_score()
        {
            var osv = new FakeOsvClient();
            osv.Matches["System.Text.Json@8.0.0"] = new() { "GHSA-8g4q-xg66-9fp4" };
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-2cwj-8chv-9pp9" };
            osv.Records["GHSA-8g4q-xg66-9fp4"] = Fixtures.SystemTextJson43485;
            osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("System.Text.Json", "8.0.0"), Pkg("log4net", "2.0.8")), default);

            Assert.Equal(new[] { "Critical", "High" }, report.Findings.Select(f => f.Severity).ToArray());
            Assert.Equal("log4net", report.Findings[0].Component);
        }

        [Fact]
        public async Task Merges_the_same_flaw_published_under_several_ids()
        {
            var osv = new FakeOsvClient();
            osv.Matches["System.Text.Json@8.0.0"] = new() { "GHSA-8g4q-xg66-9fp4", "BIT-dotnet-2024-43485" };
            osv.Records["GHSA-8g4q-xg66-9fp4"] = Fixtures.SystemTextJson43485;
            osv.Records["BIT-dotnet-2024-43485"] = Fixtures.SystemTextJson43485Duplicate;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("System.Text.Json", "8.0.0")), default);

            var f = Assert.Single(report.Findings);
            Assert.Contains("CVE-2024-43485", f.Aliases);
            Assert.Contains("BIT-dotnet-2024-43485", f.Aliases);
        }

        [Fact]
        public async Task Database_outage_is_reported_as_incomplete_not_as_clean()
        {
            var osv = new FakeOsvClient { Unreachable = true };

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            Assert.False(report.Complete);
            Assert.False(report.DatabaseReachable);
            Assert.Empty(report.Findings);
            Assert.Equal("failed", report.Components.Single().Result);
            Assert.Contains(report.Notes, n => n.Contains("could not be reached") && n.Contains("NOT"));
        }

        [Fact]
        public async Task A_single_failed_component_makes_the_scan_incomplete_but_keeps_the_rest()
        {
            var osv = new FakeOsvClient();
            osv.FailingComponents.Add("Serilog@3.1.1");
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-2cwj-8chv-9pp9" };
            osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8"), Pkg("Serilog", "3.1.1")), default);

            Assert.False(report.Complete);
            Assert.True(report.DatabaseReachable);
            Assert.Single(report.Findings);
            Assert.Equal("failed", report.Components.Single(c => c.Name == "Serilog").Result);
            Assert.Contains(report.Notes, n => n.Contains("could not be checked"));
        }

        [Fact]
        public async Task Advisory_that_does_not_cover_the_installed_version_is_only_potential()
        {
            var osv = new FakeOsvClient();
            osv.Matches["System.Text.Json@8.0.5"] = new() { "GHSA-8g4q-xg66-9fp4" };      // 8.0.5 is the fixed version
            osv.Records["GHSA-8g4q-xg66-9fp4"] = Fixtures.SystemTextJson43485;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("System.Text.Json", "8.0.5")), default);

            var f = Assert.Single(report.Findings);
            Assert.Equal("potential", f.Confidence);
            Assert.Contains("not found inside", f.ConfidenceNote);
            Assert.Contains(report.Notes, n => n.Contains("potential"));
        }

        [Fact]
        public async Task Withdrawn_advisories_are_dropped()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-gone-gone-gone" };
            osv.Records["GHSA-gone-gone-gone"] = Fixtures.Withdrawn;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            Assert.Empty(report.Findings);
        }

        [Fact]
        public async Task Cvss4_only_advisories_use_the_published_rating_and_say_so()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-v4v4-v4v4-v4v4" };
            osv.Records["GHSA-v4v4-v4v4-v4v4"] = Fixtures.V4Only;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            var f = Assert.Single(report.Findings);
            Assert.Equal("Medium", f.Severity);
            Assert.Null(f.CvssScore);
            Assert.Contains("CVSS v4", f.SeveritySource);
        }

        [Fact]
        public async Task Advisories_without_any_severity_are_unknown_not_low()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-none-none-none" };
            osv.Records["GHSA-none-none-none"] = Fixtures.NoSeverity;

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            Assert.Equal("Unknown", Assert.Single(report.Findings).Severity);
            Assert.Equal(1, report.Summary.Unknown);
            Assert.Contains(report.Notes, n => n.Contains("Unknown"));
        }

        [Fact]
        public async Task Missing_advisory_records_are_kept_visible_and_flag_the_scan_incomplete()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-unfetchable" };     // no record registered

            var report = await Scanner(osv).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            var f = Assert.Single(report.Findings);
            Assert.Equal("Unknown", f.Severity);
            Assert.Equal("potential", f.Confidence);
            Assert.False(report.Complete);
        }

        [Fact]
        public async Task Clean_components_give_a_complete_empty_report()
        {
            var report = await Scanner(new FakeOsvClient()).ScanAsync(Request(Pkg("Serilog", "3.1.1")), default);

            Assert.True(report.Complete);
            Assert.Empty(report.Findings);
            Assert.Equal(0, report.Summary.Total);
        }

        [Fact]
        public async Task Disabled_scanner_checks_nothing_and_says_so()
        {
            var osv = new FakeOsvClient();

            var report = await Scanner(osv, enabled: false).ScanAsync(Request(Pkg("log4net", "2.0.8")), default);

            Assert.False(report.Complete);
            Assert.Empty(osv.Queried);                                   // nothing was sent anywhere
            Assert.Equal("skipped", report.Components.Single().Result);
            Assert.Contains(report.Notes, n => n.Contains("disabled"));
        }

        [Fact]
        public async Task Nothing_to_scan_is_reported()
        {
            var report = await Scanner(new FakeOsvClient()).ScanAsync(new ScanRequest { IncludeApplication = false }, default);

            Assert.False(report.Complete);
            Assert.Contains(report.Notes, n => n.Contains("nothing to scan"));
        }

        [Fact]
        public async Task Reads_the_application_s_own_manifest()
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "myapp.deps.json"),
                    """{ "libraries": { "myapp/1.0.0": { "type": "project" }, "log4net/2.0.8": { "type": "package" } } }""");
                var osv = new FakeOsvClient();
                var scanner = new VulnerabilityScanner(osv, true, null, dir, "myapp");

                var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = true }, default);

                Assert.Equal("log4net", Assert.Single(osv.Queried).Name);
                Assert.Equal("application", report.Components.Single().Source);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Missing_application_manifest_is_noted()
        {
            var report = await Scanner(new FakeOsvClient()).ScanAsync(new ScanRequest { IncludeApplication = true }, default);

            Assert.Contains(report.Notes, n => n.Contains("not found"));
        }

        [Fact]
        public async Task Assembly_references_are_listed_but_never_queried()
        {
            var osv = new FakeOsvClient();
            var request = new ScanRequest
            {
                IncludeApplication = false,
                ExtraComponents = { Pkg("Serilog", "3.1.1") },
                References = { new ReferencedAssembly { Name = "System.Text.Json", Version = "8.0.0.0" } }
            };

            var report = await Scanner(osv).ScanAsync(request, default);

            Assert.Single(report.ReferencedAssemblies);
            Assert.DoesNotContain(osv.Queried, c => c.Name == "System.Text.Json");
        }
    }

    public class OsvClientTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
            public int Calls { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(Respond(request));
            }
        }

        private static OsvClient Client(StubHandler h) => new(new HttpClient(h) { BaseAddress = new Uri("https://osv.test/") });

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [Fact]
        public async Task Maps_batch_results_back_to_the_components_in_order()
        {
            var handler = new StubHandler { Respond = _ => Json("""{ "results": [ { "vulns": [ { "id": "GHSA-1" }, { "id": "GHSA-2" } ] }, { } ] }""") };
            var components = new List<ScanComponent> { new("NuGet", "a", "1.0.0", "x"), new("NuGet", "b", "2.0.0", "x") };

            var result = await Client(handler).QueryBatchAsync(components, default);

            Assert.Equal(new[] { "GHSA-1", "GHSA-2" }, result[0]!.ToArray());
            Assert.Empty(result[1]!);
        }

        [Fact]
        public async Task Server_errors_leave_the_entries_null_so_the_scanner_can_flag_them()
        {
            var handler = new StubHandler { Respond = _ => Json("oops", HttpStatusCode.InternalServerError) };

            var result = await Client(handler).QueryBatchAsync(new[] { new ScanComponent("NuGet", "a", "1.0.0", "x") }, default);

            Assert.Null(result[0]);
        }

        [Fact]
        public async Task Malformed_responses_are_treated_as_failures()
        {
            var handler = new StubHandler { Respond = _ => Json("not json at all") };

            var result = await Client(handler).QueryBatchAsync(new[] { new ScanComponent("NuGet", "a", "1.0.0", "x") }, default);

            Assert.Null(result[0]);
        }

        [Fact]
        public async Task A_page_token_is_surfaced_as_a_marker()
        {
            var handler = new StubHandler { Respond = _ => Json("""{ "results": [ { "vulns": [ { "id": "GHSA-1" } ], "next_page_token": "abc" } ] }""") };

            var result = await Client(handler).QueryBatchAsync(new[] { new ScanComponent("NuGet", "a", "1.0.0", "x") }, default);

            Assert.Contains("__more__", result[0]!);
        }

        [Fact]
        public async Task Advisory_records_are_cached()
        {
            var handler = new StubHandler { Respond = _ => Json(Fixtures.Log4Net) };
            var client = Client(handler);

            var first = await client.GetVulnerabilityAsync("GHSA-2cwj-8chv-9pp9", default);
            var second = await client.GetVulnerabilityAsync("GHSA-2cwj-8chv-9pp9", default);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task Unknown_advisories_return_null()
        {
            var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };

            Assert.Null(await Client(handler).GetVulnerabilityAsync("GHSA-missing", default));
        }
    }
}

namespace DotNetVisualizer.Web.Tests
{
    using DotNetVisualizer.Web.Models;
    using DotNetVisualizer.Web.Services.Vulnerabilities;
    using System.Text;
    using Xunit;

    public class ShippedCodeTests
    {
        private const string Deps = """
            { "targets": { ".NETCoreApp,Version=v8.0": {
                "MyApp/1.0.0": { },
                "log4net/2.0.8": { "runtime": { "lib/netstandard1.3/log4net.dll": { } } },
                "System.Net.Http/4.1.0": { "dependencies": { "System.Runtime": "4.1.0" } },
                "Native.Pkg/1.0.0": { "native": { "runtimes/win-x64/native/x.dll": { } } }
              } },
              "libraries": {
                "MyApp/1.0.0": { "type": "project" },
                "log4net/2.0.8": { "type": "package" },
                "System.Net.Http/4.1.0": { "type": "package" },
                "Native.Pkg/1.0.0": { "type": "package" },
                "NotInTarget/1.0.0": { "type": "package" }
              } }
            """;

        private static List<ScanComponent> Read(string json) => DepsJsonReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), "deps-file");

        [Fact]
        public void Knows_which_packages_contribute_runtime_assets()
        {
            var c = Read(Deps).ToDictionary(x => x.Name);

            Assert.True(c["log4net"].ShipsAssembly);
            Assert.False(c["System.Net.Http"].ShipsAssembly);
            Assert.True(c["Native.Pkg"].ShipsAssembly);
            Assert.True(c["NotInTarget"].ShipsAssembly is null or false);
        }

        [Fact]
        public void Manifests_without_targets_leave_it_unknown()
        {
            var c = Read("""{ "libraries": { "log4net/2.0.8": { "type": "package" } } }""");

            Assert.Null(Assert.Single(c).ShipsAssembly);
        }

        [Fact]
        public async Task Findings_for_framework_supplied_packages_are_only_potential()
        {
            var osv = new FakeOsvClient();
            osv.Matches["System.Net.Http@4.1.0"] = new() { "GHSA-2cwj-8chv-9pp9" };
            osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net.Replace("\"log4net\"", "\"System.Net.Http\"");
            var scanner = new VulnerabilityScanner(osv, true, null, Path.GetTempPath(), "none");

            var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = false, ExtraComponents = Read(Deps).Where(c => c.Name == "System.Net.Http").ToList() }, default);

            var f = Assert.Single(report.Findings);
            Assert.Equal("potential", f.Confidence);
            Assert.Contains("shared framework", f.ConfidenceNote);
            Assert.Equal("Critical", f.Severity);                         // severity is unchanged, only the confidence drops
            Assert.False(report.Components.Single().ShipsAssembly);
        }

        [Fact]
        public async Task Findings_for_shipped_packages_stay_confirmed()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-2cwj-8chv-9pp9" };
            osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net;
            var scanner = new VulnerabilityScanner(osv, true, null, Path.GetTempPath(), "none");

            var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = false, ExtraComponents = Read(Deps).Where(c => c.Name == "log4net").ToList() }, default);

            Assert.Equal("confirmed", Assert.Single(report.Findings).Confidence);
        }
    }
}
