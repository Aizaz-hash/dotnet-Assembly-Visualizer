using System.Text.Json.Nodes;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Cli;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;
using DotNetVisualizer.Web.Tests.Unit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    public class ExportApiTests : IClassFixture<ApiTests.AppFactory>
    {
        private readonly ApiTests.AppFactory _factory;
        public ExportApiTests(ApiTests.AppFactory factory) => _factory = factory;

        private static async Task<string> ReportJson()
        {
            var scanner = new VulnerabilityScanner(LevelFixtures.OsvWithVulnerabilityAtEveryLevel(), true, null, Path.GetTempPath(), "x", new DependencyResolver(LevelFixtures.Graph(), _ => false));
            var report = await scanner.ScanAsync(new ScanRequest { IncludeApplication = false, RootName = "MyLib", TargetFramework = ".NETStandard,Version=v2.0", References = LevelFixtures.Refs(("PkgA", "1.0.0.0")) }, default);
            return System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }

        [Theory]
        [InlineData("cyclonedx", "application/vnd.cyclonedx+json", "bomFormat")]
        [InlineData("sarif", "application/sarif+json", "runs")]
        public async Task A_report_posted_by_the_UI_comes_back_as_the_requested_format(string format, string contentType, string marker)
        {
            using var client = _factory.CreateClient();
            var response = await client.PostAsync($"/api/vulnerabilities/export/{format}?assembly=MyLib.dll",
                new System.Net.Http.StringContent(await ReportJson(), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
            Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync())![marker]);
        }

        [Fact]
        public async Task An_unknown_format_is_a_404()
        {
            using var client = _factory.CreateClient();
            var response = await client.PostAsync("/api/vulnerabilities/export/pdf", new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
