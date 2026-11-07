using DotNetVisualizer.Core.Checks;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Unit
{
    /// <summary>The rules behind the Dav PowerShell cmdlets: threshold, cycle warnings, risky-API report, version comparison.</summary>
    public class RiskThresholdTests
    {
        [Theory]
        // detected, threshold, fails?   (the table of the specification)
        [InlineData(RiskLevel.Low, RiskLevel.Low, true)]
        [InlineData(RiskLevel.Low, RiskLevel.Medium, false)]
        [InlineData(RiskLevel.Low, RiskLevel.High, false)]
        [InlineData(RiskLevel.Medium, RiskLevel.Low, true)]
        [InlineData(RiskLevel.Medium, RiskLevel.Medium, true)]
        [InlineData(RiskLevel.Medium, RiskLevel.High, false)]
        [InlineData(RiskLevel.High, RiskLevel.Low, true)]
        [InlineData(RiskLevel.High, RiskLevel.Medium, true)]
        [InlineData(RiskLevel.High, RiskLevel.High, true)]
        public void A_risk_at_or_above_the_threshold_fails(RiskLevel detected, RiskLevel threshold, bool fails) =>
            Assert.Equal(fails, RiskThreshold.Fails(detected, threshold));

        [Theory]
        [InlineData("low", RiskLevel.Low)]
        [InlineData("Medium", RiskLevel.Medium)]
        [InlineData(" HIGH ", RiskLevel.High)]
        public void The_threshold_is_parsed_in_any_case(string text, RiskLevel expected) => Assert.Equal(expected, RiskThreshold.Parse(text));

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("critical")]
        [InlineData("severe")]
        [InlineData("none")]
        public void An_unsupported_threshold_is_rejected_with_a_message_that_names_the_allowed_values(string? text)
        {
            var ex = Assert.Throws<ArgumentException>(() => RiskThreshold.Parse(text));
            Assert.Contains("low, medium or high", ex.Message);
        }

        [Theory]
        [InlineData("Critical", RiskLevel.High)]
        [InlineData("High", RiskLevel.High)]
        [InlineData("Medium", RiskLevel.Medium)]
        [InlineData("Low", RiskLevel.Low)]
        [InlineData("Unknown", RiskLevel.Medium)]
        public void Vulnerability_severities_map_onto_the_three_levels(string severity, RiskLevel level) =>
            Assert.Equal(level, RiskThreshold.FromVulnerability(severity));
    }

    public class CircularDependencyReportTests
    {
        private static CallGraph Graph() => AssemblyFile.ReadGraph(SamplePaths.CircularDependencies);

        private static List<CircularDependency> Cycles()
        {
            var graph = Graph();
            var chains = new CircularDependencyDetectorService().DetectCycles(graph.BuildAdjacencyList());
            return CircularDependencyReport.Build(graph, chains);
        }

        [Fact]
        public void A_cycle_across_three_classes_names_the_classes_and_the_methods()
        {
            var cycle = Cycles().Single(c => c.Methods.Any(m => m.Contains("Left.Step")));
            Assert.Equal(3, cycle.Classes.Count);
            Assert.Contains(cycle.Classes, c => c.EndsWith(".Left"));
            Assert.Contains(cycle.Classes, c => c.EndsWith(".Middle"));
            Assert.Contains(cycle.Classes, c => c.EndsWith(".Right"));
            Assert.Equal(3, cycle.Methods.Count);
        }

        [Fact]
        public void The_same_cycle_is_reported_once_and_recursion_is_not_a_cycle()
        {
            var cycles = Cycles();
            var keys = cycles.Select(c => string.Join("|", c.Methods.OrderBy(m => m, StringComparer.Ordinal))).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.DoesNotContain(cycles, c => c.Methods.Any(m => m.Contains("Factorial")));
            Assert.DoesNotContain(cycles, c => c.Methods.Any(m => m.Contains("Acyclic")));
        }

        [Fact]
        public void The_entry_point_outside_a_cycle_is_not_part_of_it()
        {
            Assert.DoesNotContain(Cycles(), c => c.Methods.Any(m => m.Contains("EntryOutside.Start")));
            Assert.Contains(Cycles(), c => c.Methods.Any(m => m.Contains("EntryOutside.X")));
        }

        [Fact]
        public void Two_separate_cycles_in_one_class_are_two_warnings()
        {
            var inTwoCycles = Cycles().Where(c => c.Classes.Any(k => k.EndsWith(".TwoCycles"))).ToList();
            Assert.Equal(2, inTwoCycles.Count);
        }

        [Fact]
        public void The_same_chain_found_from_another_starting_point_is_reported_once()
        {
            var graph = Graph();
            var methods = graph.MethodNodes.Where(n => n.ParentClass.EndsWith(".SimpleCycle")).Select(n => n.Id).ToList();
            Assert.Equal(3, methods.Count);
            var a = methods.OrderBy(m => m).ToList();
            var chains = new List<IReadOnlyList<string>>
            {
                new[] { a[0], a[1], a[2], a[0] },
                new[] { a[1], a[2], a[0], a[1] },
                new[] { a[2], a[0], a[1], a[2] }
            };
            Assert.Single(CircularDependencyReport.Build(graph, chains));
        }

        [Fact]
        public void The_warning_text_has_the_title_the_names_and_the_closing_line()
        {
            var text = Cycles().First().ToWarningText();
            Assert.StartsWith("[WARNING] Possible Circular Dependency Detected", text);
            Assert.Contains("Classes:", text);
            Assert.Contains("Methods:", text);
            Assert.EndsWith("Warning: These classes and methods contain a possible circular dependency.", text);
        }

        [Fact]
        public void An_assembly_without_cycles_gives_no_warning()
        {
            var graph = AssemblyFile.ReadGraph(SamplePaths.Inheritance);
            var chains = new CircularDependencyDetectorService().DetectCycles(graph.BuildAdjacencyList());
            Assert.Empty(CircularDependencyReport.Build(graph, chains));
        }
    }

    public class RiskyApiReportTests
    {
        [Fact]
        public void The_risky_sample_reports_leads_most_severe_first_with_their_callers()
        {
            var findings = RiskyApiReport.Build(AssemblyFile.ReadGraph(SamplePaths.RiskyApis));
            Assert.NotEmpty(findings);
            var levels = findings.Select(f => (int)f.Level).ToList();
            Assert.Equal(levels.OrderByDescending(x => x).ToList(), levels);
            Assert.All(findings, f => Assert.NotEmpty(f.Callers));
            Assert.Contains(findings, f => f.Level == RiskLevel.High);
        }

        [Fact]
        public void An_assembly_without_risky_calls_has_no_findings() =>
            Assert.Empty(RiskyApiReport.Build(AssemblyFile.ReadGraph(SamplePaths.Inheritance)));

        [Fact]
        public void A_file_that_is_not_a_dotnet_assembly_is_refused_with_a_clear_message()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                var ex = Assert.ThrowsAny<Exception>(() => AssemblyFile.ReadGraph(path));
                Assert.True(ex is InvalidDataException or BadImageFormatException, ex.GetType().Name);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_missing_file_is_reported_as_not_found() =>
            Assert.Throws<FileNotFoundException>(() => AssemblyFile.ReadGraph(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".dll")));
    }

    public class VersionComparisonTests
    {
        [Theory]
        [InlineData("1.0.0.0", "2.0.0.0", "major")]
        [InlineData("1.0.0.0", "1.1.0.0", "minor")]
        [InlineData("1.0.0.0", "1.0.1.0", "patch")]
        [InlineData("1.0.0.0", "1.0.0.1", "patch")]
        [InlineData("1.0.0.0", "1.0.0.0", "none")]
        [InlineData("2.0.0.0", "1.9.0.0", "none")]
        [InlineData("1.2", "1.3", "minor")]
        public void The_actual_bump_is_the_largest_part_that_went_up(string from, string to, string bump) =>
            Assert.Equal(bump, AssemblyVersionComparison.ActualBump(from, to));

        [Fact]
        public void A_breaking_change_without_a_major_bump_is_a_version_mismatch_at_high_risk()
        {
            var result = AssemblyVersionComparison.Compare(SamplePaths.ApiCompatV1, SamplePaths.ApiCompatV2);
            Assert.True(result.Breaking > 0);
            Assert.Equal("major", result.RequiredBump);
            if (result.ActualBump != "major")
            {
                Assert.True(result.VersionMismatch);
                Assert.Equal(RiskLevel.High, result.MismatchLevel);
            }
        }

        [Fact]
        public void The_fingerprints_say_whether_the_file_or_the_api_changed()
        {
            var same = AssemblyVersionComparison.Compare(SamplePaths.ApiCompatV1, SamplePaths.ApiCompatV1);
            Assert.Equal("identical file", same.Verdict);
            Assert.Equal(same.OldFileHash, same.NewFileHash);
            Assert.Equal(64, same.OldApiFingerprint.Length);
            Assert.False(same.VersionMismatch);
            Assert.Empty(same.ChangedMethods);

            var changed = AssemblyVersionComparison.Compare(SamplePaths.ApiCompatV1, SamplePaths.ApiCompatV2);
            Assert.Equal("public API changed", changed.Verdict);
            Assert.NotEqual(changed.OldApiFingerprint, changed.NewApiFingerprint);
        }

        [Fact]
        public void The_comparison_keeps_the_existing_differ_results()
        {
            var result = AssemblyVersionComparison.Compare(SamplePaths.ApiCompatV1, SamplePaths.ApiCompatV2);
            using var a = File.OpenRead(SamplePaths.ApiCompatV1);
            using var b = File.OpenRead(SamplePaths.ApiCompatV2);
            var direct = DotNetVisualizer.Core.ApiDiff.ApiDiffer.Compare(DotNetVisualizer.Core.ApiDiff.ApiSurfaceReader.Read(a), DotNetVisualizer.Core.ApiDiff.ApiSurfaceReader.Read(b));
            Assert.Equal(direct.Breaking, result.Breaking);
            Assert.Equal(direct.Compatible, result.Compatible);
            Assert.Equal(direct.Info, result.Info);
            Assert.Equal(direct.SuggestedBump, result.RequiredBump);
        }

        [Fact]
        public void The_body_hash_is_stable_for_the_same_file_and_not_affected_by_unrelated_changes()
        {
            var one = AssemblyFile.ReadGraph(SamplePaths.Metrics, hashBodies: true).MethodBodyHashes;
            var two = AssemblyFile.ReadGraph(SamplePaths.Metrics, hashBodies: true).MethodBodyHashes;
            Assert.NotEmpty(one);
            Assert.Equal(one.OrderBy(k => k.Key).ToList(), two.OrderBy(k => k.Key).ToList());
        }
    }
}
