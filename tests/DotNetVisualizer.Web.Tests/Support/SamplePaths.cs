using System.Text.Json;
using DotNetVisualizer.Web.Services;

namespace DotNetVisualizer.Web.Tests
{
    /// <summary>Where the sample assemblies are, and a helper that runs the real analyzer on one.</summary>
    public static class SamplePaths
    {
        private static string Dir => Path.Combine(AppContext.BaseDirectory, "samples");

        public static string Inheritance => Path.Combine(Dir, "Inheritance.dll");
        public static string MethodCalls => Path.Combine(Dir, "MethodCalls.dll");
        public static string CircularDependencies => Path.Combine(Dir, "CircularDependencies.dll");
        public static string RiskyApis => Path.Combine(Dir, "RiskyApis.dll");
        public static string Vulnerable => Path.Combine(Dir, "VulnerableDemo.dll");
        public static string VulnerableDeps => Path.Combine(Dir, "VulnerableDemo.deps.json");
        public static string Obfuscation => Path.Combine(Dir, "ObfuscationLike.dll");
        public static string Metrics => Path.Combine(Dir, "MetricsDemo.dll");
        public static string ApiCompatV1 => Path.Combine(Dir, "api-compat", "v1", "ApiCompat.dll");
        public static string ApiCompatV2 => Path.Combine(Dir, "api-compat", "v2", "ApiCompat.dll");

        /// <summary>The real analyzer's result for an assembly, as the JSON the browser receives.</summary>
        public static JsonElement Analyze(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Sample assembly not found - build the samples (scripts/build-samples) or run dotnet test, which builds them: " + path);
            using var stream = File.OpenRead(path);
            var result = new AssemblyAnalyzerService(new CircularDependencyDetectorService()).Analyze(stream);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return doc.RootElement.Clone();
        }
    }
}
