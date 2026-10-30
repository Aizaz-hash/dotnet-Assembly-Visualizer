using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Vulnerabilities;

namespace DotNetVisualizer.Web.Services.Cli
{
    /// <summary>
    /// Headless mode for CI: <c>dotnet dotnet-Assembly-Visualizer.dll scan App.dll --fail-on high --sarif out.sarif</c>.
    /// Runs the same dependency resolution and vulnerability scan as the web UI and returns a process exit code.
    /// </summary>
    public static class CliScan
    {
        public const int Ok = 0, FindingsAtOrAboveThreshold = 1, UsageOrInputError = 2, ScanIncomplete = 3;

        private static readonly string[] SeverityOrder = { "Critical", "High", "Medium", "Low", "Unknown" };

        public const string Usage = """
            Usage: scan <assembly.dll|.exe> [options]

              --manifest <file>     .deps.json, project.assets.json or packages.lock.json (exact versions + levels).
                                    Default: <assembly name>.deps.json next to the assembly, if it exists.
              --fail-on <level>     critical | high (default) | medium | low | none - exit code 1 when a finding is at or above it.
              --allow-incomplete    Do not fail (exit 3) when the scan could not check everything (for example no network).
              --max-level <1-4>     How deep to follow package dependencies when no manifest is given (default 4).
              --sarif <file>        Write SARIF 2.1.0 (GitHub code scanning).
              --sbom <file>         Write a CycloneDX 1.5 SBOM.
              --json <file>         Write the full scan report as JSON.
              --quiet               Only print the final result line.

            Exit codes: 0 ok, 1 findings at/above --fail-on, 2 bad arguments or unreadable file, 3 scan incomplete.
            Only package names and versions are sent (to OSV.dev and NuGet.org); nothing is installed or modified.
            """;

        public static async Task<int> RunAsync(string[] args, IOsvClient osv, INuGetClient nuget, TextWriter output, TextWriter error, CancellationToken ct = default)
        {
            string? assemblyPath = null, manifestPath = null, sarifPath = null, sbomPath = null, jsonPath = null;
            string failOn = "high";
            bool allowIncomplete = false, quiet = false;
            int maxLevel = DependencyResolver.MaxSupportedLevel;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                switch (a.ToLowerInvariant())
                {
                    case "--manifest": manifestPath = Next(); break;
                    case "--fail-on": failOn = (Next() ?? "").ToLowerInvariant(); break;
                    case "--sarif": sarifPath = Next(); break;
                    case "--sbom": sbomPath = Next(); break;
                    case "--json": jsonPath = Next(); break;
                    case "--max-level":
                        if (!int.TryParse(Next(), out maxLevel) || maxLevel < 1 || maxLevel > DependencyResolver.MaxSupportedLevel) return Fail(error, "--max-level must be 1-4.");
                        break;
                    case "--allow-incomplete": allowIncomplete = true; break;
                    case "--quiet": quiet = true; break;
                    case "-h": case "--help": output.WriteLine(Usage); return Ok;
                    default:
                        if (a.StartsWith("--")) return Fail(error, $"Unknown option {a}.");
                        if (assemblyPath != null) return Fail(error, "Only one assembly can be scanned per run.");
                        assemblyPath = a; break;
                }
            }

            if (assemblyPath == null) return Fail(error, "No assembly given.");
            if (failOn is not ("critical" or "high" or "medium" or "low" or "none")) return Fail(error, "--fail-on must be critical, high, medium, low or none.");
            if (!File.Exists(assemblyPath)) return Fail(error, $"File not found: {assemblyPath}");

            List<ReferencedAssembly> references; string assemblyName; string? targetFramework;
            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return Fail(error, $"{assemblyPath} is not a .NET assembly.");
                var md = pe.GetMetadataReader();
                references = AssemblyReferenceReader.Read(md);
                assemblyName = AssemblyReferenceReader.ReadAssemblyName(md);
                targetFramework = AssemblyReferenceReader.ReadTargetFramework(md);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException or IOException)
            {
                return Fail(error, $"Could not read {assemblyPath}: {ex.Message}");
            }
            if (string.IsNullOrEmpty(assemblyName)) assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

            // manifest: explicit, else the .deps.json that sits next to the assembly
            var extra = new List<ScanComponent>();
            string? manifestUsed = null;
            manifestPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? ".", Path.GetFileNameWithoutExtension(assemblyPath) + ".deps.json");
            bool explicitManifest = args.Any(x => x.Equals("--manifest", StringComparison.OrdinalIgnoreCase));
            if (File.Exists(manifestPath))
            {
                try
                {
                    using var ms = File.OpenRead(manifestPath);
                    extra = DependencyManifestReader.Read(ms, "deps-file");
                    manifestUsed = manifestPath;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
                {
                    return Fail(error, $"Could not read the manifest {manifestPath}: {ex.Message}");
                }
            }
            else if (explicitManifest) return Fail(error, $"Manifest not found: {manifestPath}");

            var scanner = new VulnerabilityScanner(osv, true, null, AppContext.BaseDirectory, "cli", new DependencyResolver(nuget));
            ScanReport report;
            try
            {
                report = await scanner.ScanAsync(new ScanRequest
                {
                    IncludeApplication = false, References = references, ExtraComponents = extra, RootName = assemblyName,
                    TargetFramework = targetFramework, MaxLevel = maxLevel
                }, ct);
            }
            catch (OperationCanceledException) { return Fail(error, "Cancelled."); }

            string file = Path.GetFileName(assemblyPath);
            try
            {
                if (jsonPath != null) File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
                if (sarifPath != null) File.WriteAllText(sarifPath, ReportExporters.Serialize(ReportExporters.Sarif(report, file)));
                if (sbomPath != null) File.WriteAllText(sbomPath, ReportExporters.Serialize(ReportExporters.CycloneDx(report, assemblyName)));
            }
            catch (IOException ex) { return Fail(error, "Could not write an output file: " + ex.Message); }

            if (!quiet) Print(report, file, manifestUsed, output);

            int threshold = failOn == "none" ? -1 : Array.IndexOf(SeverityOrder, char.ToUpperInvariant(failOn[0]) + failOn.Substring(1));
            int blocking = threshold < 0 ? 0 : report.Findings.Count(f => Array.IndexOf(SeverityOrder, f.Severity) <= threshold);

            if (blocking > 0)
            {
                output.WriteLine($"FAIL: {blocking} finding(s) at or above '{failOn}'.");
                return FindingsAtOrAboveThreshold;
            }
            if (!report.Complete && !allowIncomplete)
            {
                output.WriteLine("INCOMPLETE: not everything could be checked (see the notes above), so this is not a clean result. Use --allow-incomplete to accept that.");
                return ScanIncomplete;
            }
            output.WriteLine($"OK: {report.Findings.Count} finding(s), none at or above '{failOn}'" + (report.Complete ? "." : " (scan incomplete, allowed)."));
            return Ok;
        }

        private static void Print(ScanReport r, string file, string? manifest, TextWriter o)
        {
            o.WriteLine($"Scanned {file} - {r.Components.Count} dependencies listed (deepest level {r.MaxLevelResolved}), " +
                        (manifest != null ? $"exact versions from {Path.GetFileName(manifest)}." : "no manifest: package versions are estimated from the references."));
            o.WriteLine($"Findings: {r.Summary.Critical} critical, {r.Summary.High} high, {r.Summary.Medium} medium, {r.Summary.Low} low, {r.Summary.Unknown} unknown.");
            foreach (var f in r.Findings.Where(f => f.Severity is "Critical" or "High" or "Medium"))
                o.WriteLine($"  [{f.Severity,-8}] {f.Id}  {f.Component} {f.InstalledVersion}" + (f.Level != null ? $"  (level {f.Level})" : "") +
                            (f.RecommendedVersion != null ? $"  -> upgrade to {f.RecommendedVersion}" : "") + (f.Confidence == "potential" ? "  [potential]" : ""));
            foreach (var n in r.Notes) o.WriteLine("  note: " + n);
        }

        private static int Fail(TextWriter error, string message)
        {
            error.WriteLine("error: " + message);
            error.WriteLine();
            error.WriteLine(Usage);
            return UsageOrInputError;
        }
    }
}
