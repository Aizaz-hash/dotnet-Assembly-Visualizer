using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.Services;
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

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/api/analysis/limits", () => Results.Ok(new { maxFileSizeMB = maxUploadMb }));
app.MapControllers();

app.Run();
