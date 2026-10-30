using System.Text.Json;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    /// <summary>"About this assembly" and the obfuscation indicators, on the real samples.</summary>
    public class AssemblyInsightsTests
    {
        private static JsonElement Obfuscation(string path) => SamplePaths.Analyze(path).GetProperty("obfuscation");
        private static JsonElement Info(string path) => SamplePaths.Analyze(path).GetProperty("assembly");

        [Fact]
        public void The_obfuscation_like_sample_is_flagged_with_the_evidence_that_was_found()
        {
            var o = Obfuscation(SamplePaths.Obfuscation);
            Assert.True(o.GetProperty("likely").GetBoolean());
            var evidence = o.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Contains(evidence, e => e.Contains("one or two letters"));
            Assert.True(o.GetProperty("shortNames").GetInt32() >= 20);
            Assert.NotEmpty(o.GetProperty("examples").EnumerateArray());
            Assert.Contains("incomplete", o.GetProperty("note").GetString());
        }

        [Theory]
        [InlineData("inheritance")] [InlineData("method-calls")] [InlineData("circular")] [InlineData("metrics")] [InlineData("risky")] [InlineData("vulnerable")]
        public void Ordinary_code_is_not_flagged_so_the_warning_stays_meaningful(string which)
        {
            string path = which switch
            {
                "inheritance" => SamplePaths.Inheritance, "method-calls" => SamplePaths.MethodCalls, "circular" => SamplePaths.CircularDependencies,
                "metrics" => SamplePaths.Metrics, "risky" => SamplePaths.RiskyApis, _ => SamplePaths.Vulnerable
            };
            var o = Obfuscation(path);
            Assert.False(o.GetProperty("likely").GetBoolean(), string.Join("; ", o.GetProperty("evidence").EnumerateArray().Select(e => e.GetString())));
            Assert.Contains("not proof", o.GetProperty("note").GetString());                       // a clean result never claims certainty
        }

        [Fact]
        public void The_runtime_itself_is_not_mistaken_for_obfuscated_code()
        {
            var o = Obfuscation(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Linq.dll"));
            Assert.False(o.GetProperty("likely").GetBoolean());
        }

        [Fact]
        public void The_assembly_facts_come_from_the_file_header()
        {
            var v2 = Info(SamplePaths.ApiCompatV2);
            Assert.Equal("ApiCompat", v2.GetProperty("name").GetString());
            Assert.Equal("2.0.0.0", v2.GetProperty("version").GetString());
            Assert.Equal("Library (.dll)", v2.GetProperty("kind").GetString());
            Assert.Equal("Any CPU", v2.GetProperty("platform").GetString());
            Assert.True(v2.GetProperty("managedOnly").GetBoolean());
            Assert.False(v2.GetProperty("strongNamed").GetBoolean());
            Assert.False(v2.GetProperty("hasEntryPoint").GetBoolean());
            Assert.Equal(".NETCoreApp,Version=v8.0", v2.GetProperty("targetFramework").GetString());
            Assert.True(v2.GetProperty("sizeBytes").GetInt64() > 1000);
            Assert.Equal("1.0.0.0", Info(SamplePaths.ApiCompatV1).GetProperty("version").GetString());
        }

        [Fact]
        public void A_recorded_target_framework_is_reported_as_recorded_not_guessed()
        {
            Assert.False(Info(SamplePaths.Inheritance).GetProperty("targetFrameworkInferred").GetBoolean());
        }

        [Fact]
        public void An_old_dotnet_framework_assembly_gets_a_guessed_target_framework_and_says_so()
        {
            var file = Path.Combine(Environment.GetEnvironmentVariable("windir") ?? "", "Microsoft.NET", "Framework64", "v4.0.30319", "System.Core.dll");
            if (!OperatingSystem.IsWindows() || !File.Exists(file)) return;                  // needs a .NET Framework assembly (Windows only)
            var info = Info(file);
            Assert.StartsWith(".NETFramework", info.GetProperty("targetFramework").GetString());
            Assert.True(info.GetProperty("targetFrameworkInferred").GetBoolean());
        }

        [Fact]
        public void A_vulnerable_sample_lists_its_references()
        {
            Assert.True(Info(SamplePaths.Vulnerable).GetProperty("references").GetInt32() >= 3);
        }
    }
}
