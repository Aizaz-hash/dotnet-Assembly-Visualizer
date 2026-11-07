namespace DotNetVisualizer.Core.Checks;

/// <summary>One possible circular dependency: the classes and the methods that take part, each listed once and in call order.</summary>
public sealed record CircularDependency(IReadOnlyList<string> Classes, IReadOnlyList<string> Methods)
{
    public const string WarningTitle = "[WARNING] Possible Circular Dependency Detected";
    public const string WarningText = "Warning: These classes and methods contain a possible circular dependency.";

    /// <summary>The warning as a person reads it (names only; no graph dump).</summary>
    public string ToWarningText()
    {
        var lines = new List<string> { WarningTitle, "", "Classes:" };
        lines.AddRange(Classes.Select(c => "  - " + c));
        lines.Add("");
        lines.Add("Methods:");
        lines.AddRange(Methods.Select(m => "  - " + m));
        lines.Add("");
        lines.Add(WarningText);
        return string.Join(Environment.NewLine, lines);
    }
}

public static class CircularDependencyReport
{
    /// <summary>
    /// Turns the chains the detector found (A -> B -> C -> A, the first element repeated at the end) into reports. The same cycle can be
    /// found from different starting points, so a cycle is identified by its members in call order starting from the smallest id; each is reported once.
    /// </summary>
    public static List<CircularDependency> Build(CallGraph graph, IEnumerable<IReadOnlyList<string>> chains)
    {
        var classOf = graph.MethodNodes.ToDictionary(n => n.Id, n => n.ParentClass, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CircularDependency>();

        foreach (var chain in chains)
        {
            var members = chain.Count > 1 && string.Equals(chain[0], chain[chain.Count - 1], StringComparison.OrdinalIgnoreCase)
                ? chain.Take(chain.Count - 1).ToList()
                : chain.ToList();
            if (members.Count == 0) continue;

            int start = 0;
            for (int i = 1; i < members.Count; i++)
                if (string.Compare(members[i], members[start], StringComparison.OrdinalIgnoreCase) < 0) start = i;
            var ordered = members.Skip(start).Concat(members.Take(start)).ToList();

            if (!seen.Add(string.Join("\n", ordered))) continue;

            var classes = new List<string>();
            foreach (var id in ordered)
            {
                var cls = classOf.TryGetValue(id, out var c) ? c : ClassFromMethodId(id);
                if (!classes.Contains(cls, StringComparer.OrdinalIgnoreCase)) classes.Add(cls);
            }
            result.Add(new CircularDependency(classes, ordered));
        }
        return result;
    }

    /// <summary>"Ns.Class.Method(int a)" -> "Ns.Class" (only used for a method the graph has no node for).</summary>
    private static string ClassFromMethodId(string id)
    {
        int paren = id.IndexOf('(');
        var head = paren < 0 ? id : id.Substring(0, paren);
        int dot = head.LastIndexOf('.');
        return dot < 0 ? head : head.Substring(0, dot);
    }
}
