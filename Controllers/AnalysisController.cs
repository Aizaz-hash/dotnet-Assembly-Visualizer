using DotNetVisualizer.Web.Interfaces;
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
