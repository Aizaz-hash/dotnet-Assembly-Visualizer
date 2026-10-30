using System.Text.Json;
using DotNetVisualizer.Web.Services;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    // Types analysed by the tests below (they are never executed): constructors that call each other,
    // constructor chaining through base(...), a factory that only does `new`, and a class with only the implicit constructor.
    public class CtorPing { public CtorPing() { _ = new CtorPong(); } }
    public class CtorPong { public CtorPong() { _ = new CtorPing(); } }
    public class CtorBase { protected CtorBase(int x) { _ = x; } }
    public class CtorDerived : CtorBase { public CtorDerived() : base(1) { } }
    public class CtorFactory { public CtorDerived Make() => new CtorDerived(); }
    public class CtorImplicitOnly { public int Value; }

    public class ConstructorGraphTests
    {
        private static readonly JsonElement Result = Analyze();

        private static JsonElement Analyze()
        {
            var analyzer = new AssemblyAnalyzerService(new CircularDependencyDetectorService());
            using var stream = File.OpenRead(typeof(ConstructorGraphTests).Assembly.Location);
            var json = JsonSerializer.Serialize((object)analyzer.Analyze(stream), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        private static string Ctor(string type) =>
            Result.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray()
                  .Select(n => n.GetProperty("id").GetString()!)
                  .Single(id => id.StartsWith($"DotNetVisualizer.Web.Tests.{type}..ctor("));

        private static bool HasEdge(string from, string to) =>
            Result.GetProperty("methodGraph").GetProperty("edges").EnumerateArray()
                  .Any(e => e.GetProperty("from").GetString() == from && e.GetProperty("to").GetString() == to);

        [Fact]
        public void A_new_expression_creates_an_edge_to_the_constructor()
        {
            var make = Result.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray()
                .Select(n => n.GetProperty("id").GetString()!).Single(id => id.StartsWith("DotNetVisualizer.Web.Tests.CtorFactory.Make("));
            Assert.True(HasEdge(make, Ctor("CtorDerived")));
        }

        [Fact]
        public void Constructor_chaining_through_base_is_an_edge()
        {
            Assert.True(HasEdge(Ctor("CtorDerived"), Ctor("CtorBase")));
        }

        [Fact]
        public void A_cycle_that_goes_through_constructors_is_detected()
        {
            var ping = Ctor("CtorPing"); var pong = Ctor("CtorPong");
            Assert.True(HasEdge(ping, pong) && HasEdge(pong, ping));
            Assert.Contains(Result.GetProperty("deadlocks").EnumerateArray(),
                c => c.EnumerateArray().Select(x => x.GetString()).Contains(ping) && c.EnumerateArray().Select(x => x.GetString()).Contains(pong));
        }

        [Fact]
        public void Constructors_are_labelled_like_ILSpy_but_keep_a_ctor_id()
        {
            var node = Result.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray()
                .Single(n => n.GetProperty("id").GetString() == Ctor("CtorBase"));
            Assert.Equal("CtorBase(int x)", node.GetProperty("name").GetString());
            Assert.Equal("DotNetVisualizer.Web.Tests.CtorBase", node.GetProperty("parentClass").GetString());
        }

        [Fact]
        public void A_class_with_only_an_implicit_constructor_gets_no_empty_node()
        {
            var ids = Result.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!);
            Assert.DoesNotContain(ids, id => id.StartsWith("DotNetVisualizer.Web.Tests.CtorImplicitOnly."));
        }

        [Fact]
        public void Static_constructors_stay_out_of_the_graph()
        {
            var ids = Result.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!);
            Assert.DoesNotContain(ids, id => id.Contains(".cctor"));
        }
    }
}
