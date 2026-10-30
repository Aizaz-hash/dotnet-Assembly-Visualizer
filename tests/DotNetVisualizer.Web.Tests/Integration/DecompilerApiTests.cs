using DotNetVisualizer.Web.Services.Decompilation;
using Xunit;
using DotNetVisualizer.Web.Tests.Unit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    public class DecompileApiTests : IClassFixture<ApiTests.AppFactory>
    {
        private readonly ApiTests.AppFactory _factory;
        public DecompileApiTests(ApiTests.AppFactory factory) => _factory = factory;

        [Fact]
        public async Task Open_then_ask_for_a_method_by_token()
        {
            using var client = _factory.CreateClient();
            var form = new System.Net.Http.MultipartFormDataContent { { new System.Net.Http.ByteArrayContent(File.ReadAllBytes(typeof(DecompileApiTests).Assembly.Location)), "file", "t.dll" } };
            var open = await client.PostAsync("/api/decompile", form);
            Assert.Equal(System.Net.HttpStatusCode.OK, open.StatusCode);
            string id = (string)System.Text.Json.Nodes.JsonNode.Parse(await open.Content.ReadAsStringAsync())!["id"]!;

            int token = typeof(MetricsSubject).GetMethod(nameof(MetricsSubject.Simple))!.MetadataToken;
            var source = await client.GetAsync($"/api/decompile/{id}?token={token}");
            Assert.Equal(System.Net.HttpStatusCode.OK, source.StatusCode);
            Assert.StartsWith("text/plain", source.Content.Headers.ContentType!.MediaType);
            Assert.Contains("int Simple()", await source.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task An_expired_or_unknown_id_is_a_404_that_says_to_open_it_again()
        {
            using var client = _factory.CreateClient();
            var response = await client.GetAsync("/api/decompile/deadbeefdeadbeefdeadbeef?token=1");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("Open it again", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task A_file_that_is_not_an_assembly_is_a_400()
        {
            using var client = _factory.CreateClient();
            var form = new System.Net.Http.MultipartFormDataContent { { new System.Net.Http.ByteArrayContent(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "file", "bad.dll" } };
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/api/decompile", form)).StatusCode);
        }

        [Fact]
        public async Task The_analysis_lists_a_token_for_every_type_and_method_so_the_ui_can_ask_for_source()
        {
            var analyzer = new DotNetVisualizer.Web.Services.AssemblyAnalyzerService(new DotNetVisualizer.Web.Services.CircularDependencyDetectorService());
            using var stream = File.OpenRead(typeof(DecompileApiTests).Assembly.Location);
            var json = System.Text.Json.JsonSerializer.Serialize((object)analyzer.Analyze(stream), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.All(doc.RootElement.GetProperty("inheritance").GetProperty("nodes").EnumerateArray(), n => Assert.Equal(0x02, n.GetProperty("token").GetInt32() >> 24));
            Assert.All(doc.RootElement.GetProperty("methodGraph").GetProperty("nodes").EnumerateArray(), n => Assert.Equal(0x06, n.GetProperty("token").GetInt32() >> 24));
            await Task.CompletedTask;
        }
    }
}
