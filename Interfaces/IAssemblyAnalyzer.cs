namespace DotNetVisualizer.Web.Interfaces
{
    public interface IAssemblyAnalyzer
    {
        object Analyze(IFormFile file);
    }
}
