using System.Text.Json;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Vulnerabilities;
using Microsoft.AspNetCore.Mvc;

namespace DotNetVisualizer.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VulnerabilitiesController : ControllerBase
    {
        private const long MaxDepsFileBytes = 5 * 1024 * 1024;
        private const int MaxReferences = 5000;

        private readonly IVulnerabilityScanner _scanner;

        public VulnerabilitiesController(IVulnerabilityScanner scanner) => _scanner = scanner;

        /// <summary>Tells the UI whether scanning is enabled and which database is used (nothing is sent).</summary>
        [HttpGet("status")]
        public IActionResult Status() => Ok(new
        {
            enabled = _scanner.Enabled,
            database = "OSV.dev",
            host = "api.osv.dev",
            sends = "package names and versions only (never your code or assemblies)"
        });

        /// <summary>
        /// Scans the application's own packages and, optionally, a user-supplied *.deps.json (exact versions).
        /// This is a read-only lookup: nothing is executed, installed or modified.
        /// </summary>
        [HttpPost("scan")]
        [RequestSizeLimit(12 * 1024 * 1024)]
        public async Task<IActionResult> Scan(IFormFile? depsFile, [FromForm] bool includeApplication = true, [FromForm] string? references = null, CancellationToken ct = default)
        {
            var extra = new List<ScanComponent>();
            string? extraName = null;

            if (depsFile is { Length: > 0 })
            {
                if (depsFile.Length > MaxDepsFileBytes) return BadRequest("The dependency file is larger than 5 MB.");
                try
                {
                    await using var s = depsFile.OpenReadStream();
                    extra = DepsJsonReader.Read(s, "deps-file");
                    extraName = depsFile.FileName;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    return BadRequest("Could not read the dependency file: " + ex.Message);
                }
            }

            var refs = new List<ReferencedAssembly>();
            if (!string.IsNullOrWhiteSpace(references))
            {
                try
                {
                    refs = JsonSerializer.Deserialize<List<ReferencedAssembly>>(references, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                }
                catch (JsonException) { return BadRequest("The 'references' field is not valid JSON."); }
                if (refs.Count > MaxReferences) refs = refs.Take(MaxReferences).ToList();
            }

            if (!includeApplication && extra.Count == 0 && refs.Count == 0)
                return BadRequest("Select at least one source to scan: the application and/or a .deps.json file.");

            var report = await _scanner.ScanAsync(new ScanRequest
            {
                IncludeApplication = includeApplication,
                ExtraComponents = extra,
                References = refs,
                ExtraSourceName = extraName
            }, ct);
            return Ok(report);
        }
    }
}
