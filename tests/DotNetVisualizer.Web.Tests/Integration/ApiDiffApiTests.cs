using DotNetVisualizer.Web.Services.ApiDiff;
using Xunit;
using DotNetVisualizer.Web.Tests.Unit;

namespace DotNetVisualizer.Web.Tests.Integration
{
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
}
