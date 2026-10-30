#pragma warning disable CS0169, CS0414, CS0618, IDE0051
using DotNetVisualizer.Web.Services.ApiDiff;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    // Shapes read by the surface reader (never executed).
    public interface IDiffShape { void Do(int x); int Prop { get; set; } event EventHandler Changed; }
    public enum DiffColor { Red = 1, Green = 2 }
    public abstract class DiffBase
    {
        public abstract void Must();
        protected virtual int Hook() => 1;
        private int _hidden;
        internal int Inner;
        public const int Max = 10;
        public static readonly string Name = "x";
        [Obsolete] public void Old() { }
        public void Generic<T>(T value) { }
    }
    public sealed class DiffSealed
    {
        protected void NotReachable() { }
        public DiffSealed(int a) { }
        public string Value { get; } = "";
        public int this[int i] => i;
    }
    internal class DiffInternalOnly { public void Hidden() { } }

    public class ApiSurfaceReaderTests
    {
        private static readonly ApiSurface Surface = Read();

        private static ApiSurface Read()
        {
            using var s = File.OpenRead(typeof(ApiSurfaceReaderTests).Assembly.Location);
            return ApiSurfaceReader.Read(s);
        }

        private static ApiType T(string name) => Surface.Types["DotNetVisualizer.Web.Tests." + name];

        [Fact]
        public void Reads_the_assembly_identity_and_only_the_exposed_types()
        {
            Assert.Equal("DotNetVisualizer.Web.Tests", Surface.AssemblyName);
            Assert.NotEmpty(Surface.Version);
            Assert.DoesNotContain(Surface.Types.Keys, k => k.Contains("DiffInternalOnly"));
            Assert.DoesNotContain(Surface.Types.Keys, k => k.Contains("<>"));
        }

        [Fact]
        public void Classifies_the_kind_of_each_type()
        {
            Assert.Equal("interface", T("IDiffShape").Kind);
            Assert.Equal("enum", T("DiffColor").Kind);
            Assert.Equal("class", T("DiffBase").Kind);
            Assert.True(T("DiffBase").IsAbstract);
            Assert.True(T("DiffSealed").IsSealed);
        }

        [Fact]
        public void Lists_what_a_caller_can_reach_and_hides_the_rest()
        {
            var m = T("DiffBase").Members;
            Assert.Contains("M:Must()", m.Keys);
            Assert.True(m["M:Must()"].IsAbstract);
            Assert.Equal("protected", m["M:Hook()"].Access);
            Assert.True(m["M:Hook()"].IsVirtual);
            Assert.DoesNotContain(m.Keys, k => k.Contains("_hidden") || k.Contains("Inner"));
            Assert.Equal("10", m["F:Max"].Constant);
            Assert.True(m["F:Name"].IsStatic);
            Assert.True(m["M:Old()"].IsObsolete);
            Assert.Contains("M:Generic`1(!!0)", m.Keys);                                  // generic arity is part of the identity
        }

        [Fact]
        public void A_sealed_class_has_no_reachable_protected_members_and_properties_are_read_whole()
        {
            var m = T("DiffSealed").Members;
            Assert.DoesNotContain(m.Keys, k => k.Contains("NotReachable"));
            Assert.Contains("C:.ctor(int)", m.Keys);
            Assert.True(m["P:Value"].HasGetter && !m["P:Value"].HasSetter);
            Assert.Contains("P:Item[int]", m.Keys);                                         // the indexer
            Assert.DoesNotContain(m.Keys, k => k.StartsWith("M:get_"));                     // accessors are folded into the property
        }

        [Fact]
        public void Reads_interface_members_events_and_enum_values()
        {
            var i = T("IDiffShape").Members;
            Assert.True(i["M:Do(int)"].IsAbstract);
            Assert.True(i["P:Prop"].HasGetter && i["P:Prop"].HasSetter);
            Assert.Contains("E:Changed", i.Keys);
            var e = T("DiffColor").Members;
            Assert.Equal("1", e["F:Red"].Constant);
            Assert.Equal("2", e["F:Green"].Constant);
            Assert.DoesNotContain("F:value__", e.Keys);
        }

        [Fact]
        public void An_assembly_compared_with_itself_has_no_differences()
        {
            var result = ApiDiffer.Compare(Surface, Surface);
            Assert.Empty(result.Changes);
            Assert.Equal("patch", result.SuggestedBump);
        }

        [Fact]
        public void A_file_that_is_not_a_dotnet_assembly_is_rejected()
        {
            Assert.ThrowsAny<Exception>(() => ApiSurfaceReader.Read(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
        }
    }

    public class ApiDifferTests
    {
        private static ApiMember M(string display, string returns = "void", string key = "", string access = "public", bool isStatic = false, bool isAbstract = false,
            bool isVirtual = false, bool isSealed = false, bool obsolete = false, string[]? names = null, string kind = "method", string? constant = null, bool get = false, bool set = false) =>
            new(kind, key.Length > 0 ? key : "M:" + display, display, returns, access, isStatic, isAbstract, isVirtual, isSealed, obsolete, names ?? Array.Empty<string>(), constant, get, set);

        private static ApiType Type(string name, string kind = "class", string? baseType = "System.Object", string[]? interfaces = null, bool isAbstract = false, bool isSealed = false,
            string access = "public", bool obsolete = false, params ApiMember[] members) =>
            new(name, kind, access, isAbstract, isSealed, obsolete, baseType, interfaces ?? Array.Empty<string>(), members.ToDictionary(m => m.Key));

        private static ApiSurface Api(params ApiType[] types) => new("Lib", "1.0.0.0", types.ToDictionary(t => t.FullName));

        private static ApiDiffResult Diff(ApiType before, ApiType after) => ApiDiffer.Compare(Api(before), Api(after));

        private static ApiChange One(ApiDiffResult r, string severity) => Assert.Single(r.Changes, c => c.Severity == severity);

        [Fact]
        public void A_removed_type_is_breaking_and_an_added_type_is_compatible()
        {
            var r = ApiDiffer.Compare(Api(Type("A"), Type("B")), Api(Type("B"), Type("C")));
            Assert.Equal(1, r.Breaking); Assert.Equal(1, r.Compatible);
            Assert.Equal("major", r.SuggestedBump);
            Assert.Contains(r.Changes, c => c.Type == "A" && c.Change == "removed");
            Assert.Contains(r.Changes, c => c.Type == "C" && c.Change == "added");
        }

        [Fact]
        public void Only_additions_suggest_a_minor_bump_and_nothing_a_patch()
        {
            Assert.Equal("minor", ApiDiffer.Compare(Api(Type("A")), Api(Type("A", members: M("New()")))).SuggestedBump);
            Assert.Equal("patch", ApiDiffer.Compare(Api(Type("A", members: M("Same()"))), Api(Type("A", members: M("Same()")))).SuggestedBump);
        }

        [Fact]
        public void A_removed_member_is_breaking_an_added_one_is_compatible()
        {
            var r = Diff(Type("A", members: M("Gone()")), Type("A", members: M("Fresh()", key: "M:Fresh()")));
            Assert.Equal(1, r.Breaking);
            Assert.Equal(1, r.Compatible);
        }

        [Fact]
        public void A_changed_overload_is_reported_as_one_signature_change()
        {
            var r = Diff(Type("A", members: M("Save(int id)", key: "M:Save(int)")), Type("A", members: M("Save(long id)", key: "M:Save(long)")));
            var c = One(r, "breaking");
            Assert.Equal("changed", c.Change);
            Assert.Equal("Save(int id)", c.Before);
            Assert.Equal("Save(long id)", c.After);
            Assert.Empty(r.Changes.Where(x => x.Severity == "compatible"));
        }

        [Fact]
        public void A_return_type_change_is_breaking()
        {
            var c = One(Diff(Type("A", members: M("Get()", "int")), Type("A", members: M("Get()", "long"))), "breaking");
            Assert.Equal(("int", "long"), (c.Before, c.After));
        }

        [Theory]
        [InlineData("public", "protected", "breaking")]
        [InlineData("protected", "public", "compatible")]
        [InlineData("public", "protected internal", "breaking")]
        public void Narrowing_visibility_breaks_and_widening_does_not(string before, string after, string severity)
        {
            var r = Diff(Type("A", members: M("X()", access: before)), Type("A", members: M("X()", access: after)));
            Assert.Equal(severity, One(r, severity).Severity);
            Assert.Single(r.Changes);
        }

        [Fact]
        public void Static_versus_instance_and_virtual_changes_are_classified()
        {
            Assert.Equal("breaking", One(Diff(Type("A", members: M("X()")), Type("A", members: M("X()", isStatic: true))), "breaking").Severity);
            Assert.Equal("breaking", One(Diff(Type("A", members: M("X()", isVirtual: true)), Type("A", members: M("X()"))), "breaking").Severity);       // overrides stop compiling
            Assert.Equal("info", One(Diff(Type("A", members: M("X()")), Type("A", members: M("X()", isVirtual: true))), "info").Severity);
            Assert.Equal("breaking", One(Diff(Type("A", members: M("X()", isVirtual: true)), Type("A", members: M("X()", isVirtual: false, isSealed: true))), "breaking").Severity);
        }

        [Fact]
        public void Adding_an_abstract_member_to_an_interface_or_open_class_breaks_implementers_but_not_to_a_sealed_class()
        {
            var abs = M("Must()", isAbstract: true);
            Assert.Equal("breaking", One(Diff(Type("I", "interface", null), Type("I", "interface", null, members: abs)), "breaking").Severity);
            Assert.Equal("breaking", One(Diff(Type("C", isAbstract: true), Type("C", isAbstract: true, members: abs)), "breaking").Severity);
            var sealedAdd = Diff(Type("S", isSealed: true), Type("S", isSealed: true, members: M("Extra()")));
            Assert.Equal(0, sealedAdd.Breaking);
        }

        [Fact]
        public void Type_level_changes_are_breaking_where_they_should_be()
        {
            Assert.Contains(Diff(Type("A"), Type("A", isSealed: true)).Changes, c => c.Severity == "breaking" && c.Message.Contains("sealed"));
            Assert.Contains(Diff(Type("A"), Type("A", isAbstract: true)).Changes, c => c.Severity == "breaking" && c.Message.Contains("abstract"));
            Assert.Contains(Diff(Type("A", baseType: "X"), Type("A", baseType: "Y")).Changes, c => c.Severity == "breaking" && c.Message.Contains("Base type"));
            Assert.Contains(Diff(Type("A", "class"), Type("A", "struct", "System.ValueType")).Changes, c => c.Severity == "breaking" && c.Message.Contains("changed from class to struct"));
            Assert.Contains(Diff(Type("A", interfaces: new[] { "IX" }), Type("A")).Changes, c => c.Severity == "breaking" && c.Message.Contains("no longer implements IX"));
            Assert.Contains(Diff(Type("A"), Type("A", interfaces: new[] { "IX" })).Changes, c => c.Severity == "compatible" && c.Message.Contains("now implements IX"));
            Assert.Contains(Diff(Type("A", isSealed: true), Type("A")).Changes, c => c.Severity == "compatible");
        }

        [Fact]
        public void Enum_and_constant_changes_are_judged_by_their_value()
        {
            var red1 = M("Red", "DiffColor", "F:Red", kind: "field", constant: "1");
            var red2 = M("Red", "DiffColor", "F:Red", kind: "field", constant: "2");
            var blue = M("Blue", "DiffColor", "F:Blue", kind: "field", constant: "3");
            Assert.Equal("breaking", One(Diff(Type("DiffColor", "enum", "System.Enum", members: red1), Type("DiffColor", "enum", "System.Enum", members: red2)), "breaking").Severity);
            var added = Diff(Type("DiffColor", "enum", "System.Enum", members: red1), Type("DiffColor", "enum", "System.Enum", members: new[] { red1, blue }));
            Assert.Equal("compatible", One(added, "compatible").Severity);
            Assert.Contains("= 3", One(added, "compatible").Message);
        }

        [Fact]
        public void Property_accessors_parameter_names_and_obsolete_are_tracked()
        {
            var both = M("Name", "string", "P:Name", kind: "property", get: true, set: true);
            var getOnly = M("Name", "string", "P:Name", kind: "property", get: true);
            Assert.Contains(Diff(Type("A", members: both), Type("A", members: getOnly)).Changes, c => c.Severity == "breaking" && c.Message.Contains("lost its setter"));
            Assert.Contains(Diff(Type("A", members: getOnly), Type("A", members: both)).Changes, c => c.Severity == "compatible" && c.Message.Contains("gained a setter"));

            var named = Diff(Type("A", members: M("Do(int a)", key: "M:Do(int)", names: new[] { "a" })), Type("A", members: M("Do(int b)", key: "M:Do(int)", names: new[] { "b" })));
            Assert.Equal("info", One(named, "info").Severity);

            var obsolete = Diff(Type("A", members: M("Do()")), Type("A", members: M("Do()", obsolete: true)));
            Assert.Equal("info", One(obsolete, "info").Severity);
        }

        [Fact]
        public void The_result_carries_both_identities_counts_and_a_stable_order()
        {
            var r = ApiDiffer.Compare(new ApiSurface("Lib", "1.0.0.0", new() { ["Z"] = Type("Z") }), new ApiSurface("Lib", "2.0.0.0", new() { ["Y"] = Type("Y") }));
            Assert.Equal(("1.0.0.0", "2.0.0.0"), (r.OldVersion, r.NewVersion));
            Assert.Equal((1, 1), (r.OldTypes, r.NewTypes));
            Assert.Equal("breaking", r.Changes[0].Severity);                                          // breaking first
            Assert.Equal(r.Changes.Count, r.Breaking + r.Compatible + r.Info);
        }
    }

    public class ApiDiffApiTests : IClassFixture<ApiTests.AppFactory>
    {
        private readonly ApiTests.AppFactory _factory;
        public ApiDiffApiTests(ApiTests.AppFactory factory) => _factory = factory;

        private static System.Net.Http.MultipartFormDataContent Form(byte[] oldBytes, byte[] newBytes)
        {
            var form = new System.Net.Http.MultipartFormDataContent();
            form.Add(new System.Net.Http.ByteArrayContent(oldBytes), "oldFile", "old.dll");
            form.Add(new System.Net.Http.ByteArrayContent(newBytes), "newFile", "new.dll");
            return form;
        }

        [Fact]
        public async Task Comparing_an_assembly_with_itself_returns_an_empty_patch_result()
        {
            var bytes = File.ReadAllBytes(typeof(ApiDiffApiTests).Assembly.Location);
            using var client = _factory.CreateClient();
            var response = await client.PostAsync("/api/analysis/diff", Form(bytes, bytes));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(0, (int)json["breaking"]!);
            Assert.Equal("patch", (string)json["suggestedBump"]!);
        }

        [Fact]
        public async Task A_file_that_is_not_an_assembly_is_a_400_that_names_the_side()
        {
            var good = File.ReadAllBytes(typeof(ApiDiffApiTests).Assembly.Location);
            using var client = _factory.CreateClient();
            var response = await client.PostAsync("/api/analysis/diff", Form(new byte[] { 1, 2, 3, 4, 5, 6 }, good));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("old", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task A_missing_file_is_a_400()
        {
            using var client = _factory.CreateClient();
            var form = new System.Net.Http.MultipartFormDataContent { { new System.Net.Http.ByteArrayContent(new byte[] { 1 }), "oldFile", "old.dll" } };
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/api/analysis/diff", form)).StatusCode);
        }
    }

    public class PrettyTypeNameTests
    {
        [Theory]
        [InlineData("System.Func`2<!!0, !!1>", "System.Func<T0, T1>")]
        [InlineData("System.Collections.Generic.IEnumerable`1<!0>", "System.Collections.Generic.IEnumerable<T0>")]
        [InlineData("int", "int")]
        [InlineData("string[]", "string[]")]
        public void Generic_markers_are_rewritten_to_read_like_csharp(string raw, string expected) =>
            Assert.Equal(expected, ApiSurfaceReader.Pretty(raw));
    }
}
