public class CircularDependencyDetector
{
    private enum NodeColor
    {
        White, // Unvisited
        Grey,  // Currently in the active recursion stack (Exploring ancestors)
        Black  // Fully processed (Safe, verified cycle-free from this node onwards)
    }

    public static List<List<string>> DetectCycles(Dictionary<string, List<string>> adjacencyList)
    {
        var cycles = new List<List<string>>();
        var colors = new Dictionary<string, NodeColor>(StringComparer.OrdinalIgnoreCase);
        var currentPath = new List<string>();

        // Initialize every known node.
        foreach (var node in adjacencyList.Keys)
            colors[node] = NodeColor.White;

        // Also make sure dependency targets that do not have
        // their own adjacency-list entry are known to the algorithm.
        foreach (var dependencies in adjacencyList.Values)
            foreach (var dependency in dependencies)
                if (!colors.ContainsKey(dependency))
                    colors[dependency] = NodeColor.White;

        // Handle disconnected graph components.
        foreach (var node in colors.Keys)
            if (colors[node] == NodeColor.White)
                DFS(node, adjacencyList, colors, currentPath, cycles);

        return cycles;
    }

    private static void DFS(
        string currentNode,
        Dictionary<string, List<string>> adjacencyList,
        Dictionary<string, NodeColor> colors,
        List<string> currentPath,
        List<List<string>> cycles)
    {
        // Current node is now part of the active DFS path.
        colors[currentNode] = NodeColor.Grey;
        currentPath.Add(currentNode);

        if (adjacencyList.TryGetValue(currentNode, out var dependencies))
        {
            foreach (var dependency in dependencies)
            {
                if (!colors.TryGetValue(dependency, out var dependencyColor))
                {
                    dependencyColor = NodeColor.White;
                    colors[dependency] = NodeColor.White;
                }

                // BACK EDGE
                // currentNode -> dependency
                // dependency is already Grey, therefore dependency is an ancestor in the current recursion path.
                // This means we have found a circular dependency.
                if (dependencyColor == NodeColor.Grey)
                {
                    int cycleStartIndex = currentPath.IndexOf(dependency);

                    if (cycleStartIndex >= 0)
                    {
                        var cyclePath = new List<string>();

                        // The first element is the actual START of the circular dependency.
                        // Example: MethodA -> MethodB -> MethodC -> MethodA
                        // cyclePath becomes: [MethodA, MethodB, MethodC, MethodA]
                        // Therefore cyclePath[0] is always the circular dependency start.
                        for (int i = cycleStartIndex; i < currentPath.Count; i++)
                            cyclePath.Add(currentPath[i]);

                        // Close the circular path.
                        cyclePath.Add(dependency);

                        cycles.Add(cyclePath);
                    }
                }
                else if (dependencyColor == NodeColor.White)
                {
                    DFS(dependency, adjacencyList, colors, currentPath, cycles);
                }
            }
        }

        // Backtrack.
        currentPath.RemoveAt(currentPath.Count - 1);
        colors[currentNode] = NodeColor.Black;
    }
}