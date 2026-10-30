// Performance POC: runs the real analyzer over one or more assemblies and prints per-stage timings.
// usage: dotnet run -c Release --project tools/PerfPoc -- [--runs N] [--json <folder>] <dll> [<dll> ...]
//   --json <folder>  also writes the analyzer's result for each assembly (without the timings) to <folder>/<AssemblyName>.analysis.json.
//                    This is how the JavaScript test fixtures in tests/js/fixtures are produced (see scripts/update-js-fixtures.*).
using System.Diagnostics;
using System.Text.Json;
using DotNetVisualizer.Web.Services;

int runs = 5;
string? jsonFolder = null;
var files = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--runs") runs = int.Parse(args[++i]);
    else if (args[i] == "--json") jsonFolder = args[++i];
    else files.Add(args[i]);
}

var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());

foreach (var path in files)
{
    if (jsonFolder != null)
    {
        Directory.CreateDirectory(jsonFolder);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize((object)analyzer.Analyze(new MemoryStream(File.ReadAllBytes(path))), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        node.Remove("timings");
        File.WriteAllText(Path.Combine(jsonFolder, Path.GetFileNameWithoutExtension(path) + ".analysis.json"), node.ToJsonString());
        Console.WriteLine("wrote " + Path.GetFileNameWithoutExtension(path) + ".analysis.json");
        if (runs == 0) continue;
    }
    var bytes = File.ReadAllBytes(path);
    var samples = new List<Dictionary<string, double>>();
    var order = new List<string>();
    double serializeMs = 0; long jsonBytes = 0;
    int classes = 0, methods = 0, edges = 0;

    // run 0 is a warm-up (JIT) and reported separately as the cold run
    var cold = new Dictionary<string, double>();
    for (int r = 0; r <= runs; r++)
    {
        var sw = Stopwatch.StartNew();
        dynamic result = analyzer.Analyze(new MemoryStream(bytes));
        double analyzeMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var json = JsonSerializer.Serialize((object)result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        serializeMs = sw.Elapsed.TotalMilliseconds;
        jsonBytes = json.Length;

        using var doc = JsonDocument.Parse(json);
        classes = doc.RootElement.GetProperty("classes").GetArrayLength();
        methods = doc.RootElement.GetProperty("methodGraph").GetProperty("nodes").GetArrayLength();
        edges = doc.RootElement.GetProperty("methodGraph").GetProperty("edges").GetArrayLength();

        var row = new Dictionary<string, double>();
        foreach (var st in doc.RootElement.GetProperty("timings").GetProperty("stages").EnumerateArray())
        {
            var name = st.GetProperty("stage").GetString()!;
            if (!order.Contains(name)) order.Add(name);
            row[name] = st.GetProperty("ms").GetDouble();
        }
        row["JSON serialization"] = serializeMs;
        row["TOTAL (analyze + serialize)"] = analyzeMs + serializeMs;
        if (!order.Contains("JSON serialization")) { order.Add("JSON serialization"); order.Add("TOTAL (analyze + serialize)"); }
        if (r == 0) cold = row; else samples.Add(row);
    }

    Console.WriteLine($"\n=== {Path.GetFileName(path)}  ({bytes.Length / 1024.0:N0} KB, {classes} types, {methods} methods, {edges} call edges, JSON {jsonBytes / 1024.0 / 1024.0:N2} MB) ===");
    Console.WriteLine($"{"stage",-32}{"cold ms",12}{"median ms",12}{"min ms",12}");
    foreach (var stage in order)
    {
        var vals = samples.Select(s => s[stage]).OrderBy(x => x).ToList();
        Console.WriteLine($"{stage,-32}{cold[stage],12:N2}{vals[vals.Count / 2],12:N2}{vals[0],12:N2}");
    }
}
