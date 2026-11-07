using System.Text.Json;

namespace DotNetVisualizer.Core.Checks;

/// <summary>
/// The findings a team has decided to accept, written down once with a reason (<c>dav.json</c>):
/// <code>{ "risky": { "allow": [ { "id": "native-call", "reason": "wraps the OS API on purpose" } ] }, "vulns": { "ignore": [ "GHSA-xxxx-xxxx-xxxx" ] } }</code>
/// An entry is either a plain id or an object with an "id" and a "reason".
/// </summary>
public sealed class DavConfig
{
    public const string FileName = "dav.json";

    public HashSet<string> AllowedRiskyRules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> IgnoredAdvisories { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>id -> why it is accepted (for the report; empty when the file gives none)</summary>
    public Dictionary<string, string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static DavConfig Empty => new();

    /// <summary>Reads the file; a malformed file is an <see cref="InvalidDataException"/> that names it.</summary>
    public static DavConfig Load(string path)
    {
        var config = new DavConfig();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the top level must be an object");
            Read(doc.RootElement, "risky", "allow", config.AllowedRiskyRules, config.Reasons);
            Read(doc.RootElement, "vulns", "ignore", config.IgnoredAdvisories, config.Reasons);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid: {ex.Message}", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{path} is not valid: {ex.Message}", ex);
        }
        return config;
    }

    private static void Read(JsonElement root, string section, string list, HashSet<string> into, Dictionary<string, string> reasons)
    {
        if (!root.TryGetProperty(section, out var s) || s.ValueKind != JsonValueKind.Object) return;
        if (!s.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            string? id = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()
                : throw new InvalidDataException($"every entry of {section}.{list} must be an id or an object with an \"id\"");
            if (string.IsNullOrWhiteSpace(id)) continue;
            into.Add(id!);
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                reasons[id!] = r.GetString() ?? "";
        }
    }
}
