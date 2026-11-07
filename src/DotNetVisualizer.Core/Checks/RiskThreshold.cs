namespace DotNetVisualizer.Core.Checks;

/// <summary>The three levels a person can choose as the line between "report" and "fail".</summary>
public enum RiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// The rule that decides whether a detected risk fails a command: a risk fails it when its level is equal to or higher than the
/// threshold. Cycles never take part (they are warnings, not risks).
/// </summary>
public static class RiskThreshold
{
    public const string Allowed = "low, medium or high";

    /// <summary>Parses "low" / "medium" / "high" (any case). Anything else is an <see cref="ArgumentException"/> with a message that names the allowed values.</summary>
    public static RiskLevel Parse(string? text)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "low": return RiskLevel.Low;
            case "medium": return RiskLevel.Medium;
            case "high": return RiskLevel.High;
            default:
                throw new ArgumentException($"'{text}' is not a supported risk threshold. Use {Allowed}.", nameof(text));
        }
    }

    /// <summary>True when a risk of level <paramref name="detected"/> fails a command whose threshold is <paramref name="threshold"/>.</summary>
    public static bool Fails(RiskLevel detected, RiskLevel threshold) => detected >= threshold;

    /// <summary>Risky-API rules use High / Medium / Low.</summary>
    public static RiskLevel FromRule(string severity) => FromName(severity);

    /// <summary>Vulnerabilities use Critical / High / Medium / Low / Unknown: Critical counts as High, and Unknown as Medium (it could be serious, so it is not dropped at the default).</summary>
    public static RiskLevel FromVulnerability(string severity) => FromName(severity);

    private static RiskLevel FromName(string severity) => (severity ?? "").ToLowerInvariant() switch
    {
        "critical" or "high" => RiskLevel.High,
        "low" => RiskLevel.Low,
        _ => RiskLevel.Medium
    };
}
