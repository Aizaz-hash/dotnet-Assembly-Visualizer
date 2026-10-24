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
            catch (Exception ex)
            {
                return BadRequest($"Failed to process raw DLL binary metadata stream: {ex.Message}");
            }
        }
    }
}
