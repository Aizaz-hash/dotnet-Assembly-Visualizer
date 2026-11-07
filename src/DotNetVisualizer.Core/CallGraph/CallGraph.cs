using System.Reflection;
using System.Reflection.Metadata;

namespace DotNetVisualizer.Core;

public sealed record InheritanceNode(string Id, string Name, string Type, string Namespace, bool Nested, int Token);
public sealed record InheritanceEdge(string From, string To, string Relation);
public sealed record MethodNode(string Id, string Name, string ParentClass, int Token);
public sealed record MethodEdge(string From, string To, string Relation, string FromClass, string ToClass);

/// <summary>How many types, interfaces, methods and method bodies could not be read (a damaged or unusual file never looks like a clean one).</summary>
public sealed record SkippedItems(int Types, int Methods, int Interfaces, int Bodies)
{
    public bool Complete => Types + Methods + Interfaces + Bodies == 0;
}

/// <summary>One kind of risky call: how often it occurs and from which methods (the first few).</summary>
public sealed class RiskHit
{
    public int Count;
    public List<string> Callers { get; } = new();
}

/// <summary>What the engine reads from one assembly: its types, its call graph, its risky calls and the sizes of its methods.</summary>
public sealed class CallGraph
{
    public List<string> ClassList { get; init; } = new();
    public List<InheritanceNode> InheritanceNodes { get; init; } = new();
    public List<InheritanceEdge> InheritanceEdges { get; init; } = new();
    public List<MethodNode> MethodNodes { get; init; } = new();
    public List<MethodEdge> MethodEdges { get; init; } = new();
    public List<(string From, string To)> CallPairs { get; init; } = new();
    /// <summary>method id -> label and owning class</summary>
    public Dictionary<string, (string Name, string Class)> MethodDisplay { get; init; } = new();
    public Dictionary<(RiskyApiRule Rule, string Api), RiskHit> RiskHits { get; init; } = new();
    public Dictionary<string, (int Complexity, int IlSize)> MethodMetrics { get; init; } = new();
    public Dictionary<string, (MethodDefinitionHandle Handle, MethodAttributes Attributes, bool HasAttributes, string Name, string ParentClass)> MethodMeta { get; init; } = new();
    /// <summary>tokens of methods whose address is taken (ldftn / ldvirtftn)</summary>
    public HashSet<int> DelegateTargets { get; init; } = new();
    /// <summary>static constructors are not graph nodes, but what they call still counts as used</summary>
    public List<int> StaticInitBodies { get; init; } = new();
    /// <summary>method id -> hash of its body; filled only when the graph was read with hashBodies</summary>
    public Dictionary<string, string> MethodBodyHashes { get; init; } = new();
    public SkippedItems Skipped { get; init; } = new(0, 0, 0, 0);

    /// <summary>caller id -> callee ids, the form the cycle detector takes</summary>
    public Dictionary<string, List<string>> BuildAdjacencyList()
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in CallPairs)
        {
            if (!adjacency.TryGetValue(from, out var callees))
                adjacency[from] = callees = new List<string>();
            callees.Add(to);
        }
        return adjacency;
    }
}
