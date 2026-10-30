using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Integration
{
    /// <summary>Starts the real app in memory with a fake vulnerability database (no network).</summary>
    public class ApiTests : IClassFixture<ApiTests.AppFactory>
    {
        public sealed class AppFactory : WebApplicationFactory<Program>
        {
            public FakeOsvClient Osv { get; } = new();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOsvClient>();
                    services.AddSingleton<IOsvClient>(Osv);
                });
            }
        }

        private readonly AppFactory _factory;

        public ApiTests(AppFactory factory)
        {
            _factory = factory;
            _factory.Osv.Matches.Clear();
            _factory.Osv.Records.Clear();
            _factory.Osv.Unreachable = false;
        }

        private static MultipartFormDataContent Form(string? depsJson, bool includeApplication = false)
        {
            var form = new MultipartFormDataContent { { new StringContent(includeApplication ? "true" : "false"), "includeApplication" } };
            if (depsJson != null)
                form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(depsJson)), "depsFile", "app.deps.json");
            return form;
        }

        private const string VulnerableDeps = """{ "libraries": { "log4net/2.0.8": { "type": "package" } } }""";

        [Fact]
        public async Task Status_reports_the_database_and_what_is_sent()
        {
            var client = _factory.CreateClient();

            var json = await client.GetFromJsonAsync<JsonElement>("/api/vulnerabilities/status");

            Assert.True(json.GetProperty("enabled").GetBoolean());
            Assert.Equal("api.osv.dev", json.GetProperty("host").GetString());
            Assert.Contains("never your code", json.GetProperty("sends").GetString());
        }

        [Fact]
        public async Task Scan_of_a_deps_file_returns_a_prioritised_report()
        {
            _factory.Osv.Matches["log4net@2.0.8"] = new() { "GHSA-2cwj-8chv-9pp9" };
            _factory.Osv.Records["GHSA-2cwj-8chv-9pp9"] = Fixtures.Log4Net;
            var client = _factory.CreateClient();

            var response = await client.PostAsync("/api/vulnerabilities/scan", Form(VulnerableDeps));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var report = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(report.GetProperty("complete").GetBoolean());
            Assert.Equal(1, report.GetProperty("summary").GetProperty("critical").GetInt32());
            var finding = report.GetProperty("findings")[0];
            Assert.Equal("CVE-2018-1285", finding.GetProperty("id").GetString());
            Assert.Equal("Critical", finding.GetProperty("severity").GetString());
            Assert.Equal("confirmed", finding.GetProperty("confidence").GetString());
        }

        [Fact]
        public async Task Database_outage_surfaces_as_an_incomplete_report_not_an_error()
        {
            _factory.Osv.Unreachable = true;
            var client = _factory.CreateClient();

            var response = await client.PostAsync("/api/vulnerabilities/scan", Form(VulnerableDeps));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var report = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(report.GetProperty("complete").GetBoolean());
            Assert.False(report.GetProperty("databaseReachable").GetBoolean());
        }

        [Fact]
        public async Task Invalid_dependency_files_are_rejected_with_a_clear_message()
        {
            var client = _factory.CreateClient();

            var notJson = await client.PostAsync("/api/vulnerabilities/scan", Form("this is not json"));
            var wrongShape = await client.PostAsync("/api/vulnerabilities/scan", Form("""{ "hello": 1 }"""));

            Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, wrongShape.StatusCode);
            Assert.Contains("deps.json", await wrongShape.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Selecting_no_source_is_a_bad_request()
        {
            var client = _factory.CreateClient();

            var response = await client.PostAsync("/api/vulnerabilities/scan", Form(null, includeApplication: false));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Existing_analysis_endpoint_is_untouched_and_the_limits_endpoint_works()
        {
            var client = _factory.CreateClient();

            var limits = await client.GetFromJsonAsync<JsonElement>("/api/analysis/limits");
            var notAnAssembly = await client.PostAsync("/api/analysis/analyze",
                new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "x.dll" } });

            Assert.True(limits.GetProperty("maxFileSizeMB").GetInt32() > 0);
            Assert.Equal(HttpStatusCode.BadRequest, notAnAssembly.StatusCode);
        }
    }
}
