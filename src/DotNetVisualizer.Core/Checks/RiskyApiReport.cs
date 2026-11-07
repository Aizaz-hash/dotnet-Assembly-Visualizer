namespace DotNetVisualizer.Core.Checks;

/// <summary>One kind of risky call in an assembly: a lead for a security review, never a proven vulnerability.</summary>
public sealed record RiskyApiFinding(
    string RuleId, string Category, string Severity, string Title, string Api, string Why, string Advice,
    int Count, IReadOnlyList<string> Callers)
{
    public RiskLevel Level => RiskThreshold.FromRule(Severity);
}

public static class RiskyApiReport
{
    private static readonly string[] SeverityOrder = { "High", "Medium", "Low" };

    /// <summary>The risky calls of a call graph, most severe first.</summary>
    public static List<RiskyApiFinding> Build(CallGraph graph) =>
        graph.RiskHits
            .OrderBy(h => Array.IndexOf(SeverityOrder, h.Key.Rule.Severity))
            .ThenBy(h => h.Key.Rule.Category, StringComparer.Ordinal)
            .ThenByDescending(h => h.Value.Count)
            .ThenBy(h => h.Key.Api, StringComparer.Ordinal)
            .Select(h => new RiskyApiFinding(h.Key.Rule.Id, h.Key.Rule.Category, h.Key.Rule.Severity, h.Key.Rule.Title, h.Key.Api,
                h.Key.Rule.Why, h.Key.Rule.Advice, h.Value.Count, h.Value.Callers.ToList()))
            .ToList();
}
