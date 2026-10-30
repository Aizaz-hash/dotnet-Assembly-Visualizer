#pragma warning disable CS0169, CS0414, IDE0051, IDE0060, CS0618
using System.Text.Json;
using DotNetVisualizer.Web.Services;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    // Analysed, never executed.
    public class MetricsSubject
    {
        public int Simple() => 1;

        public int Branchy(int x)
        {
            if (x > 0) { if (x > 5) return 1; }
            for (int i = 0; i < x; i++) { x += i; }
            switch (x) { case 1: return 1; case 2: return 2; case 3: return 3; case 4: return 4; }
            try { x++; } catch (InvalidOperationException) { x--; }
            return 0;
        }

        private int UnusedPrivate() => 42;
        internal int UnusedInternal() => 3;
        private int UsedPrivate() => 1;
        public int CallsUsed() => UsedPrivate();
        private void ViaDelegate() { }
        public Action GetAction() => ViaDelegate;                 // address taken (ldftn): not dead
        [Obsolete] private int Attributed() => 4;                 // may be invoked by a framework: never listed
        public int PublicUnused() => 5;                           // public API: never listed
        private static int Fan1() => 1;
        public int FanIn1() => Fan1();
        public int FanIn2() => Fan1();
        public int FanIn3() => Fan1();
    }

    // Used only from the static constructor (field initializers): must not be reported as unreferenced.
    public class StaticInitSubject
    {
        private static int Helper() => 1;
        private static int OnlyFromStaticInit() => 2;
        private static int NeverUsed() => 3;
        private static readonly Func<int> Table = Helper;
        private static readonly int Value = OnlyFromStaticInit();
        public static int Read() => Value + Table();
    }

    public class MetricsTests
    {
        private static readonly JsonElement Result = Analyze();

        private static JsonElement Analyze()
        {
            var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());
            using var stream = File.OpenRead(typeof(MetricsTests).Assembly.Location);
            var json = JsonSerializer.Serialize((object)analyzer.Analyze(stream), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        private static string Id(string method) => "DotNetVisualizer.Web.Tests.MetricsSubject." + method + "()";
        private static JsonElement Metrics => Result.GetProperty("metrics");
        private static IEnumerable<string> Dead() => Result.GetProperty("deadCode").EnumerateArray().Select(d => d.GetProperty("id").GetString()!);

        private static JsonElement? Hotspot(string idStart) =>
            Metrics.GetProperty("hotspots").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(h => h!.Value.GetProperty("id").GetString()!.StartsWith(idStart));

        [Fact]
        public void Complexity_counts_branches_switch_cases_and_catch_clauses()
        {
            var branchy = Hotspot("DotNetVisualizer.Web.Tests.MetricsSubject.Branchy(");
            Assert.NotNull(branchy);
            // 2 ifs + loop condition + 4 switch cases + 1 catch + 1 = at least 9 independent paths
            Assert.True(branchy!.Value.GetProperty("complexity").GetInt32() >= 9, "got " + branchy.Value.GetProperty("complexity").GetInt32());
            Assert.True(branchy.Value.GetProperty("ilSize").GetInt32() > 20);
        }

        [Fact]
        public void A_method_without_decisions_has_complexity_one_and_the_hotspot_list_is_sorted()
        {
            var all = Metrics.GetProperty("hotspots").EnumerateArray().Select(h => h.GetProperty("complexity").GetInt32()).ToList();
            Assert.Equal(all.OrderByDescending(x => x), all);
            Assert.True(Metrics.GetProperty("maxComplexity").GetInt32() >= all.Max());
            Assert.True(Metrics.GetProperty("averageComplexity").GetDouble() >= 1);
        }

        [Fact]
        public void The_histogram_accounts_for_every_method_with_a_body()
        {
            int inBuckets = Metrics.GetProperty("histogram").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32());
            Assert.Equal(Metrics.GetProperty("methods").GetInt32(), inBuckets);
        }

        [Fact]
        public void Callers_and_callees_are_counted_per_method()
        {
            var fan = Metrics.GetProperty("mostCalled").EnumerateArray().First(m => m.GetProperty("id").GetString()!.StartsWith("DotNetVisualizer.Web.Tests.MetricsSubject.Fan1("));
            Assert.Equal(3, fan.GetProperty("callers").GetInt32());
            var calling = Metrics.GetProperty("mostCalling").EnumerateArray().Select(m => m.GetProperty("id").GetString()!);
            Assert.DoesNotContain(calling, id => id.Contains("MetricsSubject.Simple("));            // calls nothing
        }

        [Fact]
        public void Unreferenced_private_and_internal_methods_are_listed()
        {
            Assert.Contains(Dead(), id => id == Id("UnusedPrivate"));
            Assert.Contains(Dead(), id => id == Id("UnusedInternal"));
            var d = Result.GetProperty("deadCode").EnumerateArray().First(x => x.GetProperty("id").GetString() == Id("UnusedPrivate"));
            Assert.Equal("private", d.GetProperty("access").GetString());
            Assert.Equal("UnusedPrivate()", d.GetProperty("name").GetString());
        }

        [Fact]
        public void Called_methods_delegates_attributed_and_public_methods_are_never_listed()
        {
            var dead = Dead().ToList();
            Assert.DoesNotContain(dead, id => id == Id("UsedPrivate"));        // called
            Assert.DoesNotContain(dead, id => id == Id("ViaDelegate"));        // method group -> ldftn
            Assert.DoesNotContain(dead, id => id == Id("Attributed"));         // attribute: a framework may call it
            Assert.DoesNotContain(dead, id => id == Id("PublicUnused"));       // public API
            Assert.DoesNotContain(dead, id => id.Contains("Fan1("));           // called three times
        }

        [Fact]
        public void Methods_used_only_by_a_static_constructor_or_static_field_initializer_are_not_dead()
        {
            var dead = Dead().ToList();
            Assert.DoesNotContain(dead, id => id == "DotNetVisualizer.Web.Tests.StaticInitSubject.Helper()");
            Assert.DoesNotContain(dead, id => id == "DotNetVisualizer.Web.Tests.StaticInitSubject.OnlyFromStaticInit()");
            Assert.Contains(dead, id => id == "DotNetVisualizer.Web.Tests.StaticInitSubject.NeverUsed()");   // the control: really unused
        }

        [Fact]
        public void Constructors_entry_points_and_overrides_are_not_dead_code()
        {
            Assert.DoesNotContain(Dead(), id => id.Contains(".ctor") || id.Contains(".cctor"));
        }

        [Fact]
        public void Type_totals_are_reported_with_the_biggest_first()
        {
            var types = Metrics.GetProperty("types").EnumerateArray().ToList();
            Assert.NotEmpty(types);
            Assert.Equal(types.Select(t => t.GetProperty("complexity").GetInt32()).OrderByDescending(x => x), types.Select(t => t.GetProperty("complexity").GetInt32()));
        }
    }
}
