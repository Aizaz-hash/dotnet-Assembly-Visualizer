using DotNetVisualizer.Web.Services.Decompilation;
using Microsoft.AspNetCore.Mvc;

namespace DotNetVisualizer.Web.Controllers
{
    /// <summary>C# source for a type or method of an uploaded assembly (ICSharpCode.Decompiler). Read-only: nothing is executed.</summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DecompileController : ControllerBase
    {
        private readonly IDecompilerService _decompiler;

        public DecompileController(IDecompilerService decompiler) => _decompiler = decompiler;

        /// <summary>Keeps the assembly for a few minutes and returns an id; later requests send only the id and a metadata token.</summary>
        [HttpPost]
        public async Task<IActionResult> Open(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) return BadRequest("No DLL uploaded.");
            try
            {
                using var ms = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
                await file.CopyToAsync(ms, ct);
                return Ok(new { id = _decompiler.Open(ms.ToArray()) });
            }
            catch (Exception ex) when (ex is InvalidDataException or BadImageFormatException)
            {
                return BadRequest("This file is not a valid .NET assembly.");
            }
        }

        /// <summary>token = the metadata token of a type (0x02......) or method (0x06......), as listed in the analysis.</summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> Source(string id, [FromQuery] int token, CancellationToken ct)
        {
            var result = await _decompiler.DecompileAsync(id, token, ct);
            if (result == null) return NotFound("The assembly is no longer open on the server (it is kept for 15 minutes) or the member does not exist. Open it again.");
            Response.Headers["X-Decompiled-Truncated"] = result.Truncated ? "true" : "false";
            return Content(result.Code, "text/plain; charset=utf-8");
        }
    }
}
