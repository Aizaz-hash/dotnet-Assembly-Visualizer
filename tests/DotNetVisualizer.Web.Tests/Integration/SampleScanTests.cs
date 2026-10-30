using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Cli;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    /// <summary>
    /// The vulnerability scan on the real sample assemblies: references and the manifest are read from the real files, only the two
    /// network services (OSV and NuGet) are faked. Shows what a user sees for a vulnerable and for a clean assembly.
    /// </summary>
    public class SampleScanTests
    {
        private static (List<ReferencedAssembly> Refs, string Name, string? Tfm) ReadAssembly(string path)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            return (AssemblyReferenceReader.Read(md), AssemblyReferenceReader.ReadAssemblyName(md), AssemblyReferenceReader.ReadTargetFramework(md));
        }

        private static FakeOsvClient Osv()
        {
            var osv = new FakeOsvClient();
            osv.Matches["log4net@2.0.8"] = new() { "GHSA-2cwj-8chv-9pp9" };
            osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net;
            osv.Matches["System.Text.Json@8.0.0"] = new() { "GHSA-8g4q-xg66-9fp4" };
            osv.Records["GHSA-8g4q-xg66-9fp4"] = Fixtures.SystemTextJson43485;
            return osv;
        }

        // The SDK decides what goes into a generated deps.json (for example it drops or keeps System.Text.Json depending on the
        // runtime version it builds against), so the runtime-supplied case uses this fixed manifest, written the way the SDK writes a
        // package the framework supplies: listed in "libraries" but with no runtime assets.
        private const string ManifestWithRuntimeSuppliedPackage = """
        { "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
          "targets": { ".NETCoreApp,Version=v8.0": {
              "VulnerableDemo/1.0.0": { "dependencies": { "log4net": "2.0.8", "System.Text.Json": "8.0.0" } },
              "log4net/2.0.8": { "runtime": { "lib/netstandard2.0/log4net.dll": {} } },
              "System.Text.Json/8.0.0": { } } },
          "libraries": { "VulnerableDemo/1.0.0": { "type": "project" }, "log4net/2.0.8": { "type": "package" }, "System.Text.Json/8.0.0": { "type": "package" } } }
        """;

        private static async Task<ScanReport> ScanWithManifest(FakeOsvClient osv, bool useRealManifest = true)
        {
            var (refs, name, tfm) = ReadAssembly(SamplePaths.Vulnerable);
            using Stream manifest = useRealManifest ? File.OpenRead(SamplePaths.VulnerableDeps) : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ManifestWithRuntimeSuppliedPackage));
            var scanner = new VulnerabilityScanner(osv, true, null, Path.GetTempPath(), "x", new DependencyResolver(new FakeNuGetClient()));
            return await scanner.ScanAsync(new ScanRequest
            {
                IncludeApplication = false, RootName = name, TargetFramework = tfm, References = refs,
                ExtraComponents = DependencyManifestReader.Read(manifest, "deps-file")
            }, default);
        }

        [Fact]
        public async Task The_vulnerable_sample_with_its_real_manifest_gives_a_confirmed_critical_finding()
        {
            var report = await ScanWithManifest(Osv());

            var log4net = Assert.Single(report.Findings, f => f.Component == "log4net");
            Assert.Equal(("confirmed", "Critical", 1), (log4net.Confidence, log4net.Severity, log4net.Level));
            Assert.Equal("CVE-2018-1285", log4net.Id);
            Assert.Equal("2.0.10", log4net.RecommendedVersion);
        }

        [Fact]
        public async Task A_package_the_runtime_supplies_is_informational_not_confirmed()
        {
            var report = await ScanWithManifest(Osv(), useRealManifest: false);
            var stj = Assert.Single(report.Findings, f => f.Component == "System.Text.Json");
            Assert.Equal("informational", stj.Confidence);
            Assert.Contains("runtime", stj.ConfidenceNote);
        }

        [Fact]
        public async Task Without_a_manifest_the_versions_are_estimated_and_never_called_confirmed()
        {
            var (refs, name, tfm) = ReadAssembly(SamplePaths.Vulnerable);
            var nuget = new FakeNuGetClient();
            nuget.Package("log4net", "2.0.8");
            nuget.Package("Newtonsoft.Json", "12.0.1");
            var scanner = new VulnerabilityScanner(Osv(), true, null, Path.GetTempPath(), "x", new DependencyResolver(nuget));

            var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = false, RootName = name, TargetFramework = tfm, References = refs }, default);

            var f = Assert.Single(report.Findings, x => x.Component == "log4net");
            Assert.Equal("potential", f.Confidence);
            Assert.Contains("matches the referenced assembly version", f.ConfidenceNote);
            Assert.DoesNotContain(report.Findings, x => x.Confidence == "confirmed");
        }

        [Theory]
        [InlineData("inheritance")]
        [InlineData("method-calls")]
        [InlineData("circular")]
        [InlineData("metrics")]
        public async Task A_sample_that_only_references_the_runtime_has_nothing_to_check_and_says_so(string which)
        {
            string path = which switch { "inheritance" => SamplePaths.Inheritance, "method-calls" => SamplePaths.MethodCalls, "circular" => SamplePaths.CircularDependencies, _ => SamplePaths.Metrics };
            var (refs, name, tfm) = ReadAssembly(path);
            var osv = new FakeOsvClient();
            var scanner = new VulnerabilityScanner(osv, true, null, Path.GetTempPath(), "x", new DependencyResolver(new FakeNuGetClient()));

            var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = false, RootName = name, TargetFramework = tfm, References = refs }, default);

            Assert.True(report.NothingToCheck);
            Assert.True(report.Complete);
            Assert.Empty(report.Findings);
            Assert.Empty(osv.Queried);
            Assert.Contains(report.Notes, n => n.Contains("supplied by the .NET runtime"));
        }

        [Fact]
        public async Task A_database_outage_is_never_reported_as_clean_for_a_vulnerable_sample()
        {
            var osv = Osv(); osv.Unreachable = true;
            var report = await ScanWithManifest(osv);
            Assert.False(report.Complete);
            Assert.Empty(report.Findings);
            Assert.All(report.Components.Where(c => c.Resolution == "resolved"), c => Assert.Equal("Vulnerability lookup unavailable", c.Status));
        }

        [Fact]
        public async Task The_command_line_on_the_real_sample_fails_on_the_critical_finding_and_ignores_informational_ones()
        {
            var output = new StringWriter();
            var code = await CliScan.RunAsync(new[] { SamplePaths.Vulnerable, "--fail-on", "high" }, Osv(), new FakeNuGetClient(), output, new StringWriter());

            Assert.Equal(CliScan.FindingsAtOrAboveThreshold, code);                                 // the deps.json next to the dll was found automatically
            var text = output.ToString();
            Assert.Contains("exact versions from VulnerableDemo.deps.json", text);
            Assert.Contains("CVE-2018-1285", text);
            Assert.Contains("FAIL: 1 finding(s) at or above 'high'", text);                         // informational findings are never counted
            Assert.All(text.Split('\n').Where(l => l.Contains("[informational")), l => Assert.Contains("not counted", l));       // when the SDK kept System.Text.Json it is labelled, never counted
        }

        [Fact]
        public async Task The_command_line_passes_a_sample_with_no_packages()
        {
            var output = new StringWriter();
            var code = await CliScan.RunAsync(new[] { SamplePaths.Inheritance }, new FakeOsvClient(), new FakeNuGetClient(), output, new StringWriter());
            Assert.Equal(CliScan.Ok, code);
            Assert.Contains("OK: 0 finding(s)", output.ToString());
        }
    }
}
