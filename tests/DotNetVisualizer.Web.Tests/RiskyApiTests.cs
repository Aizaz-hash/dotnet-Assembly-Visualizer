#pragma warning disable SYSLIB0011, SYSLIB0021, SYSLIB0022, SYSLIB0045, SYSLIB0014, CA5350, CA5351, CA5359, CA5364, CA1416, CS0618
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DotNetVisualizer.Web.Services;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    // Analysed, never executed.
    public class RiskyUser
    {
        public object UsesBinaryFormatter() => new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
        public void StartsAProcess() => Process.Start("tool.exe");
        public void LoadsAnAssembly() => Assembly.LoadFrom("plugin.dll");
        public byte[] Md5(byte[] data) => MD5.HashData(data);
        public byte[] Sha1(byte[] data) => SHA1.HashData(data);
        public object Des() => DES.Create();
        public void TurnsOffTls() { ServicePointManager.ServerCertificateValidationCallback = (a, b, c, d) => true; }
        [DllImport("kernel32.dll")] public static extern int GetTickCount();
        public byte[] Safe(byte[] data) => SHA256.HashData(data);
    }

    public class RiskyApiTests
    {
        private static readonly JsonElement Risks = Analyze();

        private static JsonElement Analyze()
        {
            var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());
            using var stream = File.OpenRead(typeof(RiskyApiTests).Assembly.Location);
            var json = JsonSerializer.Serialize((object)analyzer.Analyze(stream), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("riskyApis").Clone();
        }

        private static IEnumerable<JsonElement> Hits(string ruleId) => Risks.EnumerateArray().Where(r => r.GetProperty("ruleId").GetString() == ruleId);

        private static bool CalledFrom(string ruleId, string method) =>
            Hits(ruleId).Any(h => h.GetProperty("callers").EnumerateArray().Any(c => c.GetString()!.StartsWith("DotNetVisualizer.Web.Tests.RiskyUser." + method + "(")));

        [Theory]
        [InlineData("deserialize-binaryformatter", "UsesBinaryFormatter", "High")]
        [InlineData("process-start", "StartsAProcess", "Medium")]
        [InlineData("assembly-load", "LoadsAnAssembly", "Medium")]
        [InlineData("weak-hash-md5", "Md5", "Low")]
        [InlineData("weak-hash-sha1", "Sha1", "Low")]
        [InlineData("weak-cipher-des", "Des", "Medium")]
        [InlineData("tls-validation", "TurnsOffTls", "Medium")]
        [InlineData("native-call", "GetTickCount", "Low")]
        public void Each_risky_use_is_reported_with_its_rule_severity_and_calling_method(string ruleId, string method, string severity)
        {
            Assert.True(CalledFrom(ruleId, method), $"{ruleId} should be reported from {method}");
            Assert.All(Hits(ruleId), h => Assert.Equal(severity, h.GetProperty("severity").GetString()));
        }

        [Fact]
        public void A_native_call_names_the_library_and_entry_point()
        {
            Assert.Contains(Hits("native-call"), h => h.GetProperty("api").GetString() == "kernel32.dll!GetTickCount");
        }

        [Fact]
        public void Safe_apis_are_not_flagged()
        {
            Assert.False(CalledFrom("weak-hash-md5", "Safe"));
            Assert.False(Risks.EnumerateArray().Any(r => r.GetProperty("callers").EnumerateArray().Any(c => c.GetString()!.Contains("RiskyUser.Safe("))));
        }

        [Fact]
        public void Findings_are_ordered_by_severity_and_carry_an_explanation()
        {
            var order = new[] { "High", "Medium", "Low" };
            var severities = Risks.EnumerateArray().Select(r => Array.IndexOf(order, r.GetProperty("severity").GetString())).ToList();
            Assert.Equal(severities.OrderBy(x => x), severities);
            Assert.All(Risks.EnumerateArray(), r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("why").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("advice").GetString()));
                Assert.True(r.GetProperty("count").GetInt32() >= 1);
            });
        }

        [Fact]
        public void Rule_lookup_prefers_a_specific_method_and_falls_back_to_the_whole_type()
        {
            Assert.NotNull(RiskyApiRules.Match("System.Diagnostics.Process", "Start"));
            Assert.Null(RiskyApiRules.Match("System.Diagnostics.Process", "GetCurrentProcess"));
            Assert.NotNull(RiskyApiRules.Match("System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", "Deserialize"));
            Assert.Null(RiskyApiRules.Match("System.Security.Cryptography.SHA256", "HashData"));
        }

        [Fact]
        public void An_assembly_with_no_risky_calls_has_an_empty_list_not_a_missing_one()
        {
            var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());
            using var stream = File.OpenRead(typeof(System.Linq.Enumerable).Assembly.Location);
            var json = JsonSerializer.Serialize((object)analyzer.Analyze(stream), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("riskyApis").ValueKind);
        }
    }
}
