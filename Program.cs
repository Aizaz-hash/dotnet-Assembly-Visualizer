using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.Services;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using DotNetVisualizer.Web.Services.Cli;
using DotNetVisualizer.Web.Services.Decompilation;
using Microsoft.AspNetCore.Http.Features;

// Headless mode for CI: "scan <assembly> ..." runs the same scan as the web UI and exits with a status code (see CliScan.Usage).
if (args.Length > 0 && args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
{
    static HttpClient Client(string envName, string fallback)
    {
        var url = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(url)) url = fallback;
        var http = new HttpClient { BaseAddress = new Uri(url.EndsWith("/") ? url : url + "/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-Assembly-Visualizer-cli");
        return http;
    }
    using var osvHttp = Client("VULN_OSV_URL", "https://api.osv.dev/");
    using var nugetHttp = Client("VULN_NUGET_URL", "https://api.nuget.org/v3-flatcontainer/");
    return await CliScan.RunAsync(args[1..], new OsvClient(osvHttp), new NuGetClient(nugetHttp), Console.Out, Console.Error);
}


var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DAV";
});

// Upload limit in MB (appsettings.json "Upload:MaxFileSizeMB" or env Upload__MaxFileSizeMB).
var maxUploadMb = builder.Configuration.GetValue("Upload:MaxFileSizeMB", 200);
var maxUploadBytes = maxUploadMb * 1024L * 1024L;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);

builder.Services.AddSingleton<ICircularDependencyDetector, CircularDependencyDetectorService>();
builder.Services.AddScoped<IAssemblyAnalyzer, AssemblyAnalyzerService>();
builder.Services.AddSingleton<IDecompilerService, DecompilerService>();

builder.Services.AddControllers();
builder.Services.AddAntiforgery();

// Vulnerability testing: read-only lookups against OSV.dev (package names and versions only).
var vulnEnabled = builder.Configuration.GetValue("Vulnerability:Enabled", true);
var osvBase = builder.Configuration.GetValue("Vulnerability:OsvBaseUrl", "https://api.osv.dev/")!;
var osvTimeout = builder.Configuration.GetValue("Vulnerability:TimeoutSeconds", 20);
builder.Services.AddHttpClient<IOsvClient, OsvClient>(c =>
{
    c.BaseAddress = new Uri(osvBase.EndsWith("/") ? osvBase : osvBase + "/");
    c.Timeout = TimeSpan.FromSeconds(osvTimeout);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-Assembly-Visualizer");
});
var nugetBase = builder.Configuration.GetValue("Vulnerability:NuGetBaseUrl", "https://api.nuget.org/v3-flatcontainer/")!;
builder.Services.AddHttpClient<INuGetClient, NuGetClient>(c =>
{
    c.BaseAddress = new Uri(nugetBase.EndsWith("/") ? nugetBase : nugetBase + "/");
    c.Timeout = TimeSpan.FromSeconds(osvTimeout);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-Assembly-Visualizer");
});
builder.Services.AddSingleton<IDependencyResolver>(sp => new DependencyResolver(sp.GetRequiredService<INuGetClient>()));
builder.Services.AddSingleton<IVulnerabilityScanner>(sp => new VulnerabilityScanner(
    sp.GetRequiredService<IOsvClient>(), vulnEnabled,
    sp.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider,
    AppContext.BaseDirectory,
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "dotnet-Assembly-Visualizer",
    sp.GetRequiredService<IDependencyResolver>()));

var app = builder.Build();

app.UseDefaultFiles();
// The scripts change between versions and are loaded without a version in their URL: the browser must ask the server (a cheap 304) instead of
// reusing an old copy, otherwise a new app.js can run next to an old views.js.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "no-cache" });

app.UseRouting();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/api/analysis/limits", () => Results.Ok(new { maxFileSizeMB = maxUploadMb }));
app.MapControllers();

app.Run();
return 0;

public partial class Program { }   // lets the integration tests start the app with WebApplicationFactory
