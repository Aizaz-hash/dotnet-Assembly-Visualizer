using System.Management.Automation;
using DotNetVisualizer.Core;
using DotNetVisualizer.Core.Checks;
using DotNetVisualizer.Core.Vulnerabilities;

namespace Dav
{
    /// <summary>One cycle, with the file it was found in.</summary>
    public sealed class CircularDependencyInfo
    {
        public string Assembly { get; init; } = "";
        public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
    }

    /// <summary>A risky API lead with the file it was found in.</summary>
    public sealed class RiskyApiInfo
    {
        public string Assembly { get; init; } = "";
        public string RuleId { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public string Api { get; init; } = "";
        public int Count { get; init; }
        public IReadOnlyList<string> Callers { get; init; } = Array.Empty<string>();
        public string Why { get; init; } = "";
        public string Advice { get; init; } = "";
        /// <summary>True when dav.json or -Allow accepts this rule.</summary>
        public bool Allowed { get; init; }
    }

    /// <summary>A known vulnerability in a dependency.</summary>
    public sealed class VulnerabilityInfo
    {
        public string Assembly { get; init; } = "";
        public string Id { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Package { get; init; } = "";
        public string Version { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string? FixedIn { get; init; }
        public string Summary { get; init; } = "";
        public int? Level { get; init; }
        public string? DependencyPath { get; init; }
        public string Url { get; init; } = "";
        /// <summary>True when dav.json or -Ignore accepts this advisory.</summary>
        public bool Ignored { get; init; }
    }

    internal static class Output
    {
        public static IEnumerable<RiskyApiInfo> Risky(string assembly, CallGraph graph, DavConfig config, IEnumerable<string>? allow)
        {
            var allowed = new HashSet<string>(config.AllowedRiskyRules, StringComparer.OrdinalIgnoreCase);
            if (allow != null) foreach (var a in allow) allowed.Add(a);
            return RiskyApiReport.Build(graph).Select(f => new RiskyApiInfo
            {
                Assembly = assembly, RuleId = f.RuleId, Severity = f.Severity, Category = f.Category, Title = f.Title, Api = f.Api, Count = f.Count,
                Callers = f.Callers, Why = f.Why, Advice = f.Advice, Allowed = allowed.Contains(f.RuleId)
            });
        }

        public static IEnumerable<VulnerabilityInfo> Vulnerabilities(string assembly, VulnerabilityCheckResult result, DavConfig config, IEnumerable<string>? ignore)
        {
            var ignored = new HashSet<string>(config.IgnoredAdvisories, StringComparer.OrdinalIgnoreCase);
            if (ignore != null) foreach (var i in ignore) ignored.Add(i);
            return result.Report.Findings.Select(f => new VulnerabilityInfo
            {
                Assembly = assembly, Id = f.Id, Severity = f.Severity, Package = f.Component, Version = f.InstalledVersion, Confidence = f.Confidence,
                FixedIn = f.RecommendedVersion, Summary = f.Summary, Level = f.Level, DependencyPath = f.DependencyPath, Url = f.DatabaseUrl,
                Ignored = ignored.Contains(f.Id) || ignored.Contains(f.DatabaseId) || f.Aliases.Any(ignored.Contains)
            });
        }

        public static IEnumerable<CircularDependencyInfo> Cycles(string assembly, CallGraph graph)
        {
            var chains = new CircularDependencyDetectorService().DetectCycles(graph.BuildAdjacencyList());
            return CircularDependencyReport.Build(graph, chains).Select(c => new CircularDependencyInfo { Assembly = assembly, Classes = c.Classes, Methods = c.Methods });
        }

        public static string WarningText(CircularDependencyInfo c) => new CircularDependency(c.Classes, c.Methods).ToWarningText();
    }

    /// <summary>Lists the circular dependencies of an assembly. They are warnings: the command never fails because of them.</summary>
    [Cmdlet(VerbsCommon.Get, "DavCircularDependency")]
    [OutputType(typeof(CircularDependencyInfo))]
    public sealed class GetDavCircularDependencyCommand : DavCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [Alias("FullName", "PSPath")]
        public string[] Path { get; set; } = Array.Empty<string>();

        protected override void ProcessRecord()
        {
            foreach (var p in Path)
            {
                var file = ResolveFile(p);
                var graph = Read(file, () => AssemblyFile.ReadGraph(file));
                foreach (var cycle in Output.Cycles(file, graph))
                {
                    WriteWarning(Environment.NewLine + Output.WarningText(cycle));
                    WriteObject(cycle);
                }
            }
        }
    }

    /// <summary>Lists the calls to risky APIs. They are leads for a review; the command fails when one is at or above the threshold and not accepted.</summary>
    [Cmdlet(VerbsCommon.Get, "DavRiskyApi")]
    [OutputType(typeof(RiskyApiInfo))]
    public sealed class GetDavRiskyApiCommand : DavCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [Alias("FullName", "PSPath")]
        public string[] Path { get; set; } = Array.Empty<string>();

        [Parameter(HelpMessage = ThresholdHelp)]
        [Alias("RiskThreshold")]
        [ValidateSet("low", "medium", "high", IgnoreCase = true)]
        public string? FailOn { get; set; }

        [Parameter(HelpMessage = "Rule ids you accept on purpose (see also dav.json).")]
        public string[]? Allow { get; set; }

        [Parameter(HelpMessage = "A dav.json file; default: dav.json in the current directory, if it exists.")]
        public string? Config { get; set; }

        private readonly List<RiskyApiInfo> _failing = new();

        protected override void ProcessRecord()
        {
            var threshold = Threshold(FailOn);
            var config = LoadConfig(Config);
            foreach (var p in Path)
            {
                var file = ResolveFile(p);
                var graph = Read(file, () => AssemblyFile.ReadGraph(file));
                foreach (var info in Output.Risky(file, graph, config, Allow))
                {
                    WriteObject(info);
                    if (!info.Allowed && RiskThreshold.Fails(RiskThreshold.FromRule(info.Severity), threshold)) _failing.Add(info);
                }
            }
        }

        protected override void EndProcessing()
        {
            if (_failing.Count > 0)
                Fail($"{_failing.Count} risky API lead(s) at or above '{(FailOn ?? "low").ToLowerInvariant()}': " +
                     string.Join(", ", _failing.Select(f => f.RuleId).Distinct()) + ". Review them, or accept a rule with -Allow or dav.json.", _failing);
        }
    }

    /// <summary>Looks up the known vulnerabilities of the packages an assembly depends on (only package names and versions are sent to OSV.dev and NuGet.org).</summary>
    [Cmdlet(VerbsCommon.Get, "DavVulnerability")]
    [OutputType(typeof(VulnerabilityInfo))]
    public sealed class GetDavVulnerabilityCommand : DavCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [Alias("FullName", "PSPath")]
        public string[] Path { get; set; } = Array.Empty<string>();

        [Parameter(HelpMessage = ThresholdHelp)]
        [Alias("RiskThreshold")]
        [ValidateSet("low", "medium", "high", IgnoreCase = true)]
        public string? FailOn { get; set; }

        [Parameter(HelpMessage = "A .deps.json, project.assets.json or packages.lock.json with exact versions. Default: the .deps.json next to the assembly.")]
        public string? Manifest { get; set; }

        [Parameter(HelpMessage = "How deep to follow package dependencies when there is no manifest (1-4, default 4).")]
        [ValidateRange(1, 4)]
        public int MaxLevel { get; set; } = 4;

        [Parameter(HelpMessage = "Advisory ids you accept on purpose (see also dav.json).")]
        public string[]? Ignore { get; set; }

        [Parameter(HelpMessage = "Do not fail when not everything could be checked (for example no network).")]
        public SwitchParameter AllowIncomplete { get; set; }

        [Parameter(HelpMessage = "A dav.json file; default: dav.json in the current directory, if it exists.")]
        public string? Config { get; set; }

        private readonly List<VulnerabilityInfo> _failing = new();
        private readonly List<string> _incomplete = new();

        protected override void ProcessRecord()
        {
            var threshold = Threshold(FailOn);
            var config = LoadConfig(Config);
            foreach (var p in Path)
            {
                var file = ResolveFile(p);
                var options = new VulnerabilityCheckOptions { ManifestPath = Manifest == null ? null : ResolveFile(Manifest), MaxLevel = MaxLevel };
                VulnerabilityCheckResult result;
                try
                {
                    result = VulnerabilityCheck.RunAsync(file, options, Cancellation).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { throw new PipelineStoppedException(); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Terminate(new ErrorRecord(ex, "UnreadableAssembly", ex is FileNotFoundException ? ErrorCategory.ObjectNotFound : ErrorCategory.ReadError, file));
                    throw;
                }

                foreach (var note in result.Report.Notes) WriteVerbose(note);
                if (!result.Report.Complete) _incomplete.Add(file);
                var counted = new HashSet<string>(result.Counted.Select(f => f.Id + "|" + f.Component + "|" + f.InstalledVersion));
                foreach (var info in Output.Vulnerabilities(file, result, config, Ignore))
                {
                    WriteObject(info);
                    // informational findings (packages the runtime supplies) are shown but never fail
                    if (!info.Ignored && info.Confidence != "informational" && RiskThreshold.Fails(RiskThreshold.FromVulnerability(info.Severity), threshold)) _failing.Add(info);
                }
            }
        }

        protected override void EndProcessing()
        {
            if (_failing.Count > 0)
                Fail($"{_failing.Count} vulnerability finding(s) at or above '{(FailOn ?? "low").ToLowerInvariant()}': " +
                     string.Join(", ", _failing.Select(f => f.Id).Distinct()) + ".", _failing);
            if (_incomplete.Count > 0 && !AllowIncomplete)
                Fail("INCOMPLETE: not everything could be checked (run with -Verbose for the notes), so this is not a clean result. Use -AllowIncomplete to accept that.", _incomplete);
        }
    }

    /// <summary>Compares two builds of an assembly. Fails when the version number is too small for the changes.</summary>
    [Cmdlet(VerbsData.Compare, "DavVersion")]
    [OutputType(typeof(VersionComparison))]
    public sealed class CompareDavVersionCommand : DavCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        [Alias("Old", "Before")]
        public string OldPath { get; set; } = "";

        [Parameter(Mandatory = true, Position = 1)]
        [Alias("New", "After")]
        public string NewPath { get; set; } = "";

        [Parameter(HelpMessage = "The lowest risk level of a version mismatch that makes the command fail: low (default), medium or high. " +
                                 "A missing major bump (breaking changes) is high, a missing minor bump (new API) is medium.")]
        [Alias("RiskThreshold")]
        [ValidateSet("low", "medium", "high", IgnoreCase = true)]
        public string? FailOn { get; set; }

        protected override void ProcessRecord()
        {
            var threshold = Threshold(FailOn);
            string oldFile = ResolveFile(OldPath), newFile = ResolveFile(NewPath);
            var result = Read(newFile, () => AssemblyVersionComparison.Compare(oldFile, newFile));
            WriteObject(result);
            if (result.VersionMismatch && result.MismatchLevel is { } level && RiskThreshold.Fails(level, threshold))
                Fail($"Version mismatch: {result.OldVersion} -> {result.NewVersion} is a '{result.ActualBump}' change, but the API changes require a '{result.RequiredBump}' bump " +
                     $"({result.Breaking} breaking, {result.Compatible} compatible).", result);
        }
    }

    /// <summary>What <see cref="InvokeDavCheckCommand"/> found.</summary>
    public sealed class DavCheckResult
    {
        public string Assembly { get; init; } = "";
        public IReadOnlyList<RiskyApiInfo> RiskyApis { get; init; } = Array.Empty<RiskyApiInfo>();
        public IReadOnlyList<VulnerabilityInfo> Vulnerabilities { get; init; } = Array.Empty<VulnerabilityInfo>();
        public IReadOnlyList<CircularDependencyInfo> CircularDependencies { get; init; } = Array.Empty<CircularDependencyInfo>();
        public bool VulnerabilityCheckComplete { get; init; } = true;
        public bool Passed { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    }

    /// <summary>Runs the risky-API, vulnerability and circular-dependency checks on one assembly. Cycles only warn.</summary>
    [Cmdlet(VerbsLifecycle.Invoke, "DavCheck")]
    [OutputType(typeof(DavCheckResult))]
    public sealed class InvokeDavCheckCommand : DavCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [Alias("FullName", "PSPath")]
        public string[] Path { get; set; } = Array.Empty<string>();

        [Parameter(HelpMessage = ThresholdHelp)]
        [Alias("RiskThreshold")]
        [ValidateSet("low", "medium", "high", IgnoreCase = true)]
        public string? FailOn { get; set; }

        [Parameter(HelpMessage = "Rule ids of risky APIs you accept on purpose.")]
        public string[]? Allow { get; set; }

        [Parameter(HelpMessage = "Advisory ids you accept on purpose.")]
        public string[]? Ignore { get; set; }

        [Parameter(HelpMessage = "A .deps.json, project.assets.json or packages.lock.json with exact versions.")]
        public string? Manifest { get; set; }

        [Parameter(HelpMessage = "Do not fail when the vulnerability lookup could not finish (for example no network).")]
        public SwitchParameter AllowIncomplete { get; set; }

        [Parameter(HelpMessage = "Do not look up vulnerabilities (nothing leaves the machine).")]
        public SwitchParameter SkipVulnerabilities { get; set; }

        [Parameter(HelpMessage = "A dav.json file; default: dav.json in the current directory, if it exists.")]
        public string? Config { get; set; }

        private readonly List<DavCheckResult> _failed = new();

        protected override void ProcessRecord()
        {
            var threshold = Threshold(FailOn);
            var config = LoadConfig(Config);
            foreach (var p in Path)
            {
                var file = ResolveFile(p);
                var graph = Read(file, () => AssemblyFile.ReadGraph(file));
                var reasons = new List<string>();

                var risky = Output.Risky(file, graph, config, Allow).ToList();
                var failingRisky = risky.Where(r => !r.Allowed && RiskThreshold.Fails(RiskThreshold.FromRule(r.Severity), threshold)).ToList();
                if (failingRisky.Count > 0) reasons.Add($"{failingRisky.Count} risky API lead(s) at or above '{(FailOn ?? "low").ToLowerInvariant()}': {string.Join(", ", failingRisky.Select(r => r.RuleId).Distinct())}");

                var vulns = new List<VulnerabilityInfo>();
                bool complete = true;
                if (!SkipVulnerabilities)
                {
                    try
                    {
                        var options = new VulnerabilityCheckOptions { ManifestPath = Manifest == null ? null : ResolveFile(Manifest) };
                        var result = VulnerabilityCheck.RunAsync(file, options, Cancellation).GetAwaiter().GetResult();
                        complete = result.Report.Complete;
                        vulns = Output.Vulnerabilities(file, result, config, Ignore).ToList();
                    }
                    catch (OperationCanceledException) { throw new PipelineStoppedException(); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
                    {
                        Terminate(new ErrorRecord(ex, "UnreadableManifest", ErrorCategory.ReadError, file));
                        throw;
                    }
                    var failingVulns = vulns.Where(v => !v.Ignored && v.Confidence != "informational" && RiskThreshold.Fails(RiskThreshold.FromVulnerability(v.Severity), threshold)).ToList();
                    if (failingVulns.Count > 0) reasons.Add($"{failingVulns.Count} vulnerability finding(s) at or above '{(FailOn ?? "low").ToLowerInvariant()}': {string.Join(", ", failingVulns.Select(v => v.Id).Distinct())}");
                    if (!complete && !AllowIncomplete) reasons.Add("the vulnerability lookup is incomplete, so this is not a clean result (use -AllowIncomplete to accept that)");
                }

                var cycles = Output.Cycles(file, graph).ToList();
                foreach (var c in cycles) WriteWarning(Environment.NewLine + Output.WarningText(c));

                var summary = new DavCheckResult
                {
                    Assembly = file, RiskyApis = risky, Vulnerabilities = vulns, CircularDependencies = cycles, VulnerabilityCheckComplete = complete,
                    Passed = reasons.Count == 0, Reasons = reasons
                };
                WriteObject(summary);
                if (!summary.Passed) _failed.Add(summary);
            }
        }

        protected override void EndProcessing()
        {
            if (_failed.Count > 0)
                Fail("Check failed: " + string.Join("; ", _failed.SelectMany(f => f.Reasons)) + ".", _failed);
        }
    }
}
