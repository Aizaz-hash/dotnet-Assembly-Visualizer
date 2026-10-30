using System.Text.Json;
using DotNetVisualizer.Web.Services;
using DotNetVisualizer.Web.Services.ApiDiff;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    /// <summary>
    /// The real analyzer on the real sample assemblies (samples/*). Each test states what the sample's comments promise, so the
    /// samples, their documentation and the product cannot drift apart. Nothing here is mocked.
    /// </summary>
    public class SampleAssemblyTests
    {
        private static IEnumerable<string> MethodIds(JsonElement r) => r.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!);
        private static string Id(JsonElement r, string classAndMethod) => MethodIds(r).Single(id => id.StartsWith(classAndMethod + "(", StringComparison.Ordinal));
        private static IEnumerable<(string From, string To)> Calls(JsonElement r) =>
            r.GetProperty("methodGraph").GetProperty("edges").EnumerateArray().Select(e => (e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!));
        private static bool Calls(JsonElement r, string from, string to) => Calls(r).Any(e => e.From.StartsWith(from + "(") && e.To.StartsWith(to + "("));
        private static IEnumerable<(string From, string To, string Relation)> Inherits(JsonElement r) =>
            r.GetProperty("inheritance").GetProperty("edges").EnumerateArray().Select(e => (e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!, e.GetProperty("relation").GetString()!));
        private static List<List<string>> Cycles(JsonElement r) =>
            r.GetProperty("deadlocks").EnumerateArray().Select(c => c.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();

        // ------------------------------------------------------------------------------------------------ inheritance

        [Fact]
        public void Inheritance_sample_has_the_four_level_chain_the_depth_selector_depends_on()
        {
            var r = SamplePaths.Analyze(SamplePaths.Inheritance);
            var edges = Inherits(r).ToList();
            const string ns = "Samples.Inheritance.";

            Assert.Contains(edges, e => e.From == ns + "RoundedSquare" && e.To == ns + "Square" && e.Relation == "Inherits");
            Assert.Contains(edges, e => e.From == ns + "Square" && e.To == ns + "Rectangle");
            Assert.Contains(edges, e => e.From == ns + "Rectangle" && e.To == ns + "Polygon");
            Assert.Contains(edges, e => e.From == ns + "Polygon" && e.To == ns + "Shape");
            Assert.Contains(edges, e => e.From == ns + "Circle" && e.To == ns + "Shape");
        }

        [Fact]
        public void Inheritance_sample_interfaces_are_reported_as_implements_not_inherits()
        {
            var edges = Inherits(SamplePaths.Analyze(SamplePaths.Inheritance)).ToList();
            Assert.Contains(edges, e => e.From == "Samples.Inheritance.Shape" && e.To == "Samples.Inheritance.IShape" && e.Relation == "Implements");
            Assert.Contains(edges, e => e.From == "Samples.Inheritance.Shape" && e.To == "Samples.Inheritance.INamed" && e.Relation == "Implements");
            Assert.Contains(edges, e => e.From == "Samples.Inheritance.Square" && e.To.StartsWith("System.IComparable") && e.Relation == "Implements");
        }

        [Fact]
        public void A_class_that_derives_from_a_generic_instantiation_keeps_its_base_type()
        {
            // UserRepository : Repository<User>. The base is a type specification in the metadata; it must resolve to the generic type, not to a placeholder.
            var edges = Inherits(SamplePaths.Analyze(SamplePaths.Inheritance)).ToList();
            Assert.Contains(edges, e => e.From == "Samples.Inheritance.UserRepository" && e.To == "Samples.Inheritance.Repository`1" && e.Relation == "Inherits");
            Assert.DoesNotContain(edges, e => e.To == "GenericTypeSpecification");
        }

        [Fact]
        public void Nested_and_generic_types_are_listed_with_stable_names()
        {
            var classes = SamplePaths.Analyze(SamplePaths.Inheritance).GetProperty("classes").EnumerateArray().Select(c => c.GetString()!).ToList();
            Assert.Contains("Samples.Inheritance.Repository`1", classes);
            Assert.Contains(classes, c => c.StartsWith("Samples.Inheritance.Outer") && c.EndsWith("Deepest"));
            Assert.Equal(classes.Count, classes.Distinct().Count());
        }

        [Fact]
        public void A_nested_type_is_in_the_namespace_of_its_outermost_type_not_in_a_namespace_named_after_its_parents()
        {
            // regression: the browser used to cut the namespace off the full name, so Outer and Outer.Inner became fake namespaces
            var nodes = SamplePaths.Analyze(SamplePaths.Inheritance).GetProperty("inheritance").GetProperty("nodes").EnumerateArray().ToList();
            var deepest = nodes.Single(n => n.GetProperty("id").GetString() == "Samples.Inheritance.Outer.Inner.Deepest");
            Assert.Equal("Samples.Inheritance", deepest.GetProperty("namespace").GetString());
            Assert.True(deepest.GetProperty("nested").GetBoolean());
            Assert.False(nodes.Single(n => n.GetProperty("id").GetString() == "Samples.Inheritance.Outer").GetProperty("nested").GetBoolean());
            Assert.All(nodes, n => Assert.Equal("Samples.Inheritance", n.GetProperty("namespace").GetString()));
        }

        // ------------------------------------------------------------------------------------------------ method calls

        [Fact]
        public void Method_call_sample_chain_has_the_edges_the_depth_model_needs()
        {
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            const string p = "Samples.MethodCalls.Pipeline.";
            Assert.True(Calls(r, p + "Run", p + "Load"));
            Assert.True(Calls(r, p + "Load", p + "Parse"));
            Assert.True(Calls(r, p + "Parse", p + "Validate"));
        }

        [Fact]
        public void Each_overload_is_its_own_node_and_is_called_individually()
        {
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            var formats = MethodIds(r).Where(id => id.StartsWith("Samples.MethodCalls.Formatter.Format(")).ToList();
            Assert.Equal(3, formats.Count);
            Assert.Equal(3, Calls(r).Where(e => e.From.StartsWith("Samples.MethodCalls.Pipeline.Run(") && e.To.StartsWith("Samples.MethodCalls.Formatter.Format(")).Select(e => e.To).Distinct().Count());
        }

        [Fact]
        public void Fan_in_and_fan_out_are_counted_per_distinct_method()
        {
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            Assert.True(Calls(r).Where(e => e.To.StartsWith("Samples.MethodCalls.Helper.Log(")).Select(e => e.From).Distinct().Count() >= 4);
            Assert.True(Calls(r).Where(e => e.From.StartsWith("Samples.MethodCalls.Orchestrator.Everything(")).Select(e => e.To).Distinct().Count() >= 6);
        }

        [Fact]
        public void Framework_calls_are_not_edges_and_recursion_is_not_a_self_edge()
        {
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            Assert.DoesNotContain(Calls(r), e => e.To.Contains("Console") || e.To.StartsWith("System."));
            Assert.DoesNotContain(Calls(r), e => e.From == e.To);
        }

        [Fact]
        public void Known_limitation_calls_through_an_interface_or_a_delegate_are_not_edges()
        {
            // documented in the README and in samples/method-calls: this test pins the limit so a change in behaviour is noticed and documented
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            Assert.DoesNotContain(Calls(r), e => e.From.StartsWith("Samples.MethodCalls.Dispatcher.Go("));
            Assert.DoesNotContain(Calls(r), e => e.From.StartsWith("Samples.MethodCalls.Dispatcher.ViaDelegate("));
            Assert.Contains(Calls(r), e => e.From.StartsWith("Samples.MethodCalls.Worker.Work(") && e.To.StartsWith("Samples.MethodCalls.Helper.Log("));   // the implementation itself is analysed normally
        }

        [Fact]
        public void A_constructor_is_a_node_when_it_calls_something_or_is_called_and_otherwise_not()
        {
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            Assert.Contains(Calls(r), e => e.From.StartsWith("Samples.MethodCalls.Service..ctor(") && e.To.StartsWith("Samples.MethodCalls.Service.Initialise("));
            Assert.Contains(Calls(r), e => e.From.StartsWith("Samples.MethodCalls.Pipeline.Run(") && e.To.StartsWith("Samples.MethodCalls.Formatter..ctor("));   // new Formatter(): the empty constructor is called, so it is listed
            Assert.DoesNotContain(MethodIds(r), id => id.StartsWith("Samples.MethodCalls.Queries..ctor("));                                              // nothing creates a Queries and it calls nothing
        }

        [Fact]
        public void A_generic_method_is_one_node_reached_from_both_callers()
        {
            var r = SamplePaths.Analyze(SamplePaths.MethodCalls);
            var mapNodes = MethodIds(r).Where(id => id.Contains("Mapper.Map")).ToList();
            Assert.Single(mapNodes);
            Assert.Equal(2, Calls(r).Count(e => e.To == mapNodes[0]));
        }

        // ------------------------------------------------------------------------------------------------ circular dependencies

        [Fact]
        public void Circular_sample_reports_every_documented_cycle()
        {
            var cycles = Cycles(SamplePaths.Analyze(SamplePaths.CircularDependencies));
            bool Has(params string[] methods) => cycles.Any(c => methods.All(m => c.Any(x => x.StartsWith("Samples.Cycles." + m + "("))));

            Assert.True(Has("SimpleCycle.A", "SimpleCycle.B", "SimpleCycle.C"), "simple cycle");
            Assert.True(Has("Left.Step", "Middle.Step", "Right.Step"), "cycle across three classes");
            Assert.True(Has("MutualRecursion.IsEven", "MutualRecursion.IsOdd"), "mutual recursion");
            Assert.True(Has("Ping..ctor", "Pong..ctor"), "cycle through constructors");
            Assert.True(Has("EntryOutside.X", "EntryOutside.Y"), "cycle reached from outside");
            Assert.True(Has("TwoCycles.P", "TwoCycles.Q"), "first of two cycles");
            Assert.True(Has("TwoCycles.R", "TwoCycles.S", "TwoCycles.T"), "second of two cycles");
        }

        [Fact]
        public void The_entry_method_of_a_cycle_is_not_part_of_it_and_the_two_cycles_stay_separate()
        {
            var cycles = Cycles(SamplePaths.Analyze(SamplePaths.CircularDependencies));
            Assert.DoesNotContain(cycles, c => c.Any(x => x.StartsWith("Samples.Cycles.EntryOutside.Start(")));
            Assert.DoesNotContain(cycles, c => c.Any(x => x.Contains("TwoCycles.P(")) && c.Any(x => x.Contains("TwoCycles.R(")));
        }

        [Fact]
        public void Recursion_and_plain_chains_are_not_circular_dependencies()
        {
            var cycles = Cycles(SamplePaths.Analyze(SamplePaths.CircularDependencies));
            Assert.DoesNotContain(cycles, c => c.Any(x => x.Contains("Recursion.Factorial(")));
            Assert.DoesNotContain(cycles, c => c.Any(x => x.Contains("Acyclic.")));
            Assert.Equal(7, cycles.Count);
        }

        [Fact]
        public void Two_namespaces_that_use_each_other_show_up_as_class_dependencies_in_both_directions()
        {
            // the Architecture view builds its namespace graph from exactly these edges
            var r = SamplePaths.Analyze(SamplePaths.CircularDependencies);
            var classEdges = r.GetProperty("methodGraph").GetProperty("edges").EnumerateArray().Select(e => (e.GetProperty("fromClass").GetString()!, e.GetProperty("toClass").GetString()!)).ToList();
            Assert.Contains(classEdges, e => e.Item1 == "Samples.Cycles.Billing.Invoice" && e.Item2 == "Samples.Cycles.Shipping.Parcel");
            Assert.Contains(classEdges, e => e.Item1 == "Samples.Cycles.Shipping.Parcel" && e.Item2 == "Samples.Cycles.Billing.Invoice");
        }

        // ------------------------------------------------------------------------------------------------ risky APIs

        private static JsonElement[] Risks(string path) => SamplePaths.Analyze(path).GetProperty("riskyApis").EnumerateArray().ToArray();
        private static bool Flags(JsonElement[] risks, string rule, string callerClass, string callerMethod) =>
            risks.Any(x => x.GetProperty("ruleId").GetString() == rule && x.GetProperty("callers").EnumerateArray().Any(c => c.GetString()!.StartsWith($"Samples.RiskyApis.{callerClass}.{callerMethod}(")));

        [Theory]
        [InlineData("deserialize-binaryformatter", "Deserialization", "Load", "High")]
        [InlineData("process-start", "Launcher", "Run", "Medium")]
        [InlineData("assembly-load", "PluginHost", "Load", "Medium")]
        [InlineData("weak-hash-md5", "Hashing", "Checksum", "Low")]
        [InlineData("weak-hash-sha1", "Hashing", "Legacy", "Low")]
        [InlineData("weak-cipher-des", "Encryption", "Old", "Medium")]
        [InlineData("tls-validation", "Network", "TrustEverything", "Medium")]
        [InlineData("native-call", "Native", "GetTickCount", "Low")]
        public void Every_documented_risky_use_is_found_with_its_severity(string rule, string cls, string method, string severity)
        {
            var risks = Risks(SamplePaths.RiskyApis);
            Assert.True(Flags(risks, rule, cls, method), $"{rule} from {cls}.{method}");
            Assert.All(risks.Where(x => x.GetProperty("ruleId").GetString() == rule), x => Assert.Equal(severity, x.GetProperty("severity").GetString()));
        }

        [Fact]
        public void The_safe_class_produces_no_finding_and_every_finding_explains_itself()
        {
            var risks = Risks(SamplePaths.RiskyApis);
            Assert.DoesNotContain(risks, x => x.GetProperty("callers").EnumerateArray().Any(c => c.GetString()!.Contains(".SafeCode.")));
            Assert.All(risks, x =>
            {
                Assert.False(string.IsNullOrWhiteSpace(x.GetProperty("why").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(x.GetProperty("advice").GetString()));
            });
        }

        [Fact]
        public void An_assembly_without_risky_calls_has_an_empty_list()
        {
            Assert.Empty(Risks(SamplePaths.Inheritance));
            Assert.Empty(Risks(SamplePaths.CircularDependencies));
        }

        // ------------------------------------------------------------------------------------------------ metrics

        [Fact]
        public void Metrics_sample_ranks_the_branchy_method_first_and_finds_the_unreferenced_ones()
        {
            var r = SamplePaths.Analyze(SamplePaths.Metrics);
            var top = r.GetProperty("metrics").GetProperty("hotspots")[0];
            Assert.StartsWith("Samples.Metrics.Rules.Classify(", top.GetProperty("id").GetString());
            Assert.True(top.GetProperty("complexity").GetInt32() >= 10);

            var trivial = r.GetProperty("metrics").GetProperty("hotspots").EnumerateArray().First(h => h.GetProperty("id").GetString()!.StartsWith("Samples.Metrics.Rules.Trivial("));
            Assert.Equal(1, trivial.GetProperty("complexity").GetInt32());

            var dead = r.GetProperty("deadCode").EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
            Assert.Contains(dead, d => d.StartsWith("Samples.Metrics.Cleanup.NeverCalled("));
            Assert.Contains(dead, d => d.StartsWith("Samples.Metrics.Cleanup.InternalNeverCalled("));
            foreach (var used in new[] { "UsedPrivate", "ViaDelegate", "Marked", "PublicApi", "OnlyFromStaticInit" })
                Assert.DoesNotContain(dead, d => d.StartsWith($"Samples.Metrics.Cleanup.{used}("));
        }

        // ------------------------------------------------------------------------------------------------ obfuscation-like

        [Fact]
        public void Renamed_symbols_are_listed_as_they_are_and_look_alike_names_stay_distinct()
        {
            var classes = SamplePaths.Analyze(SamplePaths.Obfuscation).GetProperty("classes").EnumerateArray().Select(c => c.GetString()!).ToList();
            Assert.Contains("Samples.Obfuscation.a", classes);
            Assert.Contains("Samples.Obfuscation.а", classes);                 // Cyrillic "а"
            Assert.Equal(classes.Count, classes.Distinct().Count());
        }

        [Fact]
        public void Inheritance_and_calls_survive_renaming()
        {
            var r = SamplePaths.Analyze(SamplePaths.Obfuscation);
            Assert.Contains(Inherits(r), e => e.From == "Samples.Obfuscation.а" && e.To == "Samples.Obfuscation.a");
            Assert.True(Calls(r, "Samples.Obfuscation.a.b", "Samples.Obfuscation.a.d"));
            Assert.True(Calls(r, "Samples.Obfuscation.а.b2", "Samples.Obfuscation.a.b"));
        }

        [Fact]
        public void Calls_inside_flattened_control_flow_are_still_found_and_the_complexity_shows_it()
        {
            var r = SamplePaths.Analyze(SamplePaths.Obfuscation);
            foreach (var step in new[] { "Step1", "Step2", "Step3" })
                Assert.True(Calls(r, "Samples.Obfuscation.Flattened.Compute", "Samples.Obfuscation.Flattened." + step), step);
            var compute = r.GetProperty("metrics").GetProperty("hotspots").EnumerateArray().First(h => h.GetProperty("id").GetString()!.StartsWith("Samples.Obfuscation.Flattened.Compute("));
            Assert.True(compute.GetProperty("complexity").GetInt32() >= 5);
        }

        [Fact]
        public void A_call_made_through_reflection_is_not_an_edge_and_that_is_the_documented_limit()
        {
            var r = SamplePaths.Analyze(SamplePaths.Obfuscation);
            Assert.DoesNotContain(Calls(r), e => e.From.StartsWith("Samples.Obfuscation.Indirect.Run(") && e.To.StartsWith("Samples.Obfuscation.Target.Secret("));
        }

        // ------------------------------------------------------------------------------------------------ API compatibility

        [Fact]
        public void The_two_api_compat_versions_differ_exactly_as_documented()
        {
            ApiSurface Read(string p) { using var s = File.OpenRead(p); return ApiSurfaceReader.Read(s); }
            var result = ApiDiffer.Compare(Read(SamplePaths.ApiCompatV1), Read(SamplePaths.ApiCompatV2));

            bool Has(string severity, string type, string contains) => result.Changes.Any(c => c.Severity == severity && c.Type == "Samples.ApiCompat." + type && c.Message.Contains(contains));
            Assert.True(Has("breaking", "Customer", "now sealed"));
            Assert.True(Has("breaking", "Customer", "lost its setter"));
            Assert.True(Has("breaking", "Customer", "Age"));                       // removed
            Assert.True(Has("breaking", "Customer", "Save"));                      // Save(int) -> Save(long)
            Assert.True(Has("breaking", "Customer", "changed from string to int"));
            Assert.True(Has("breaking", "Customer", "static to instance"));
            Assert.True(Has("breaking", "Customer", "Constant"));
            Assert.True(Has("info", "Customer", "[Obsolete]"));
            Assert.True(Has("compatible", "Customer", "Rename"));
            Assert.True(Has("breaking", "IStore", "Abstract"));
            Assert.True(Has("breaking", "Level", "changed from 2 to 3"));
            Assert.True(Has("compatible", "Level", "Critical"));
            Assert.True(Has("breaking", "Plugin", "no longer virtual"));
            Assert.True(Has("breaking", "Utility", "public to protected"));
            Assert.True(Has("breaking", "Removed", "removed"));
            Assert.True(Has("compatible", "Fresh", "New class"));
            Assert.DoesNotContain(result.Changes, c => c.Type == "Samples.ApiCompat.Unchanged");
            Assert.Equal("major", result.SuggestedBump);
            Assert.Equal((12, 3, 1, 16), (result.Breaking, result.Compatible, result.Info, result.Changes.Count));        // the numbers samples/README.md states
            Assert.Equal(("1.0.0.0", "2.0.0.0"), (result.OldVersion, result.NewVersion));
        }

        // ------------------------------------------------------------------------------------------------ diagnostics and unusual inputs

        [Theory]
        [InlineData("inheritance")] [InlineData("method-calls")] [InlineData("circular")] [InlineData("metrics")]
        [InlineData("risky")] [InlineData("obfuscation")] [InlineData("vulnerable")]
        public void A_healthy_sample_reports_complete_diagnostics_so_the_warning_never_cries_wolf(string which)
        {
            string path = which switch
            {
                "inheritance" => SamplePaths.Inheritance, "method-calls" => SamplePaths.MethodCalls, "circular" => SamplePaths.CircularDependencies,
                "metrics" => SamplePaths.Metrics, "risky" => SamplePaths.RiskyApis, "obfuscation" => SamplePaths.Obfuscation, _ => SamplePaths.Vulnerable
            };
            var d = SamplePaths.Analyze(path).GetProperty("diagnostics");
            Assert.True(d.GetProperty("complete").GetBoolean());
            Assert.False(d.GetProperty("referenceAssembly").GetBoolean());
            Assert.Equal(0, d.GetProperty("skippedTypes").GetInt32() + d.GetProperty("skippedMethods").GetInt32() + d.GetProperty("unreadableBodies").GetInt32());
        }

        [Fact]
        public void A_reference_assembly_is_flagged_because_it_has_no_real_code()
        {
            // the SDK's reference pack: System.Runtime.dll there is API only
            string? root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            var pack = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(pack)) return;                                            // no SDK reference pack on this machine
            var file = Directory.GetDirectories(pack).Select(v => Path.Combine(v, "ref", "net8.0", "System.Runtime.dll")).FirstOrDefault(File.Exists);
            if (file == null) return;
            Assert.True(SamplePaths.Analyze(file).GetProperty("diagnostics").GetProperty("referenceAssembly").GetBoolean());
        }

        [Fact]
        public void A_native_library_is_rejected_with_a_message_that_says_what_it_is()
        {
            var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            if (!OperatingSystem.IsWindows() || !File.Exists(kernel32)) return;             // a native PE to try exists on Windows only
            var ex = Assert.Throws<InvalidDataException>(() => SamplePaths.Analyze(kernel32));
            Assert.Contains("not a .NET assembly", ex.Message);
            Assert.Contains("native", ex.Message);
        }

        [Fact]
        public void Garbage_and_truncated_files_are_rejected_instead_of_producing_a_result()
        {
            var good = File.ReadAllBytes(SamplePaths.Inheritance);
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 0x4d }, good.Take(good.Length * 4 / 10).ToArray(), System.Text.Encoding.UTF8.GetBytes("not an assembly at all") })
                Assert.ThrowsAny<Exception>(() => new AssemblyAnalyzerService(new CircularDependencyDetectorService()).Analyze(new MemoryStream(bytes)));
        }

        [Fact]
        public void An_assembly_compared_with_itself_is_a_patch()
        {
            using var a = File.OpenRead(SamplePaths.ApiCompatV1); using var b = File.OpenRead(SamplePaths.ApiCompatV1);
            Assert.Equal("patch", ApiDiffer.Compare(ApiSurfaceReader.Read(a), ApiSurfaceReader.Read(b)).SuggestedBump);
        }
    }
}
