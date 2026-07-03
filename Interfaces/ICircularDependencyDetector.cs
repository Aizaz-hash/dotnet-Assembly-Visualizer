namespace DotNetVisualizer.Web.Interfaces
{
    public interface ICircularDependencyDetector
    {
        List<List<string>> DetectCycles(Dictionary<string, List<string>> adjacencyList);
    }
}
