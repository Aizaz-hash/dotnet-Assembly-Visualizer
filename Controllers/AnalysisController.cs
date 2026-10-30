using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.Services.ApiDiff;
using Microsoft.AspNetCore.Mvc;

namespace DotNetVisualizer.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AnalysisController : ControllerBase
    {
        private readonly IAssemblyAnalyzer _analyzer;

        public AnalysisController(IAssemblyAnalyzer analyzer)
        {
            _analyzer = analyzer;
        }

        /// <summary>Compares the public API of two versions of an assembly (metadata only; nothing is loaded or run).</summary>
        [HttpPost("diff")]
        public IActionResult Diff(IFormFile oldFile, IFormFile newFile)
        {
            if (oldFile == null || oldFile.Length == 0 || newFile == null || newFile.Length == 0)
                return BadRequest("Both the old and the new assembly are needed.");

            ApiSurface Read(IFormFile f, string which)
            {
                try
                {
                    using var s = f.OpenReadStream();
                    return ApiSurfaceReader.Read(s);
                }
                catch (Exception ex) when (ex is InvalidDataException or BadImageFormatException)
                {
                    throw new InvalidDataException($"The {which} file is not a valid .NET assembly.");
                }
            }

            try { return Ok(ApiDiffer.Compare(Read(oldFile, "old"), Read(newFile, "new"))); }
            catch (InvalidDataException ex) { return BadRequest(ex.Message); }
        }

        [HttpPost("analyze")]
        public IActionResult AnalyzeAssembly(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No DLL uploaded.");

            try
            {
                var result = _analyzer.Analyze(file);
                return Ok(result);
            }
            catch (InvalidDataException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (BadImageFormatException)
            {
                return BadRequest("This file is not a valid .NET assembly (native, corrupt or unsupported DLL).");
            }
            catch (Exception ex)
            {
                return BadRequest($"Could not read this file as a .NET assembly: {ex.Message}");
            }
        }
    }
}
