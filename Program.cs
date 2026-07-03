using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.Services;


var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DAV";
});

builder.Services.AddSingleton<ICircularDependencyDetector, CircularDependencyDetectorService>();
builder.Services.AddScoped<IAssemblyAnalyzer, AssemblyAnalyzerService>();

builder.Services.AddControllers();
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAntiforgery();

app.MapControllers();

app.Run();