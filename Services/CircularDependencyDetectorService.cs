using DotNetVisualizer.Web.Interfaces;

namespace DotNetVisualizer.Web.Services
{
    public class CircularDependencyDetectorService : ICircularDependencyDetector
    {
        private enum NodeColor
        {
            White, // Unvisited
            Grey,  // In active recursion stack
            Black  // Fully verified cycle-free
        }

        public List<List<string>> DetectCycles(Dictionary<string, List<string>> adjacencyList)
        {
            var cycles = new List<List<string>>();
            var colors = new Dictionary<string, NodeColor>(StringComparer.OrdinalIgnoreCase);
            var currentPath = new List<string>();

            foreach (var node in adjacencyList.Keys)
            {
                colors[node] = NodeColor.White;
            }

            foreach (var node in adjacencyList.Keys)
            {
                if (colors[node] == NodeColor.White)
                {
                    DFS(node, adjacencyList, colors, currentPath, cycles);
                }
            }

            return cycles;
        }

        private void DFS(string u,Dictionary<string, List<string>> adjList, Dictionary<string, NodeColor> colors, List<string> currentPath, List<List<string>> cycles)
        {
            colors[u] = NodeColor.Grey;
            currentPath.Add(u);

            if (adjList.TryGetValue(u, out var neighbors))
            {
                foreach (var v in neighbors)
                {
                    if (!colors.TryGetValue(v, out var neighborColor))
                    {
                        neighborColor = NodeColor.White;
                    }

                    if (neighborColor == NodeColor.Grey)
                    {
                        int cycleStartIndex = currentPath.IndexOf(v);
                        if (cycleStartIndex != -1)
                        {
                            var cyclePath = new List<string>();
                            for (int i = cycleStartIndex; i < currentPath.Count; i++)
                            {
                                cyclePath.Add(currentPath[i]);
                            }
                            cyclePath.Add(v);
                            cycles.Add(cyclePath);
                        }
                    }
                    else if (neighborColor == NodeColor.White)
                    {
                        DFS(v, adjList, colors, currentPath, cycles);
                    }
                }
            }

            currentPath.RemoveAt(currentPath.Count - 1);
            colors[u] = NodeColor.Black;
        }
    }
}
