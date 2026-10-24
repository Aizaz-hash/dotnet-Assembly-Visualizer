using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.Services;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Microsoft.AspNetCore.Http.Features;


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
builder.Services.AddSingleton<IVulnerabilityScanner>(sp => new VulnerabilityScanner(
    sp.GetRequiredService<IOsvClient>(), vulnEnabled,
    sp.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider,
    AppContext.BaseDirectory,
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "dotnet-Assembly-Visualizer"));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/api/analysis/limits", () => Results.Ok(new { maxFileSizeMB = maxUploadMb }));
app.MapControllers();

app.Run();

public partial class Program { }   // lets the integration tests start the app with WebApplicationFactory
