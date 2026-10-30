using System.Text.Json;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    /// <summary>
    /// The JavaScript tests read analysis JSON that was produced by this analyzer from the sample assemblies (tests/js/fixtures).
    /// If the analyzer or a sample changes without regenerating them, the JS tests would silently test yesterday's behaviour -
    /// this test fails instead. Regenerate with scripts/update-js-fixtures.sh (or .ps1).
    /// </summary>
    public class FixtureTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "dotnet-Assembly-Visualizer.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
        }

        private static JsonElement Fixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "js", "fixtures", name + ".analysis.json"))).RootElement;

        private static SortedSet<string> Strings(JsonElement e, params string[] path)
        {
            foreach (var p in path) e = e.GetProperty(p);
            return new SortedSet<string>(e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? x.GetProperty("id").GetString()! : x.GetString()!), StringComparer.Ordinal);
        }

        // a cycle is identified by its set of methods (rotations and direction of reporting do not matter)
        private static SortedSet<string> CycleKeys(JsonElement r) =>
            new(r.GetProperty("deadlocks").EnumerateArray().Select(c => string.Join("|", c.EnumerateArray().Select(x => x.GetString()!).Distinct().OrderBy(x => x, StringComparer.Ordinal))), StringComparer.Ordinal);

        [Theory]
        [InlineData("CircularDependencies")]
        [InlineData("Inheritance")]
        [InlineData("MethodCalls")]
        public void The_checked_in_fixture_matches_what_the_analyzer_produces_now(string name)
        {
            var path = name switch { "CircularDependencies" => SamplePaths.CircularDependencies, "Inheritance" => SamplePaths.Inheritance, _ => SamplePaths.MethodCalls };
            var now = SamplePaths.Analyze(path);
            var fixture = Fixture(name);

            Assert.Equal(Strings(now, "classes"), Strings(fixture, "classes"));
            Assert.Equal(Strings(now, "methodGraph", "nodes"), Strings(fixture, "methodGraph", "nodes"));
            Assert.Equal(CycleKeys(now), CycleKeys(fixture));
            Assert.Equal(now.GetProperty("methodGraph").GetProperty("edges").GetArrayLength(), fixture.GetProperty("methodGraph").GetProperty("edges").GetArrayLength());
            Assert.Equal(now.GetProperty("inheritance").GetProperty("edges").GetArrayLength(), fixture.GetProperty("inheritance").GetProperty("edges").GetArrayLength());
        }
    }
}
