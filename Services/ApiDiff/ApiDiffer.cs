namespace DotNetVisualizer.Web.Services.ApiDiff;

/// <param name="Severity">breaking | compatible | info</param>
/// <param name="Change">removed | added | changed</param>
public sealed record ApiChange(string Severity, string Change, string Type, string? Member, string Message, string? Before, string? After);

public sealed class ApiDiffResult
{
    public string OldName { get; init; } = "";
    public string OldVersion { get; init; } = "";
    public string NewName { get; init; } = "";
    public string NewVersion { get; init; } = "";
    public int OldTypes { get; init; }
    public int NewTypes { get; init; }
    public int Breaking { get; init; }
    public int Compatible { get; init; }
    public int Info { get; init; }
    public List<ApiChange> Changes { get; init; } = new();
    /// <summary>What a semantic-versioning reading of the changes suggests: major (breaking), minor (additions), patch (nothing public changed).</summary>
    public string SuggestedBump { get; init; } = "patch";
}

/// <summary>
/// Compares the public API of two versions of an assembly and classifies each difference. "Breaking" means code that compiled
/// and ran against the old version can fail to compile or to run against the new one; it is judged from metadata only, so
/// behavioural changes are invisible here.
/// </summary>
public static class ApiDiffer
{
    private static readonly string[] Order = { "breaking", "compatible", "info" };

    public static ApiDiffResult Compare(ApiSurface oldApi, ApiSurface newApi)
    {
        var changes = new List<ApiChange>();
        void Add(string severity, string change, string type, string? member, string message, string? before = null, string? after = null) =>
            changes.Add(new ApiChange(severity, change, type, member, message, before, after));

        foreach (var (name, o) in oldApi.Types.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!newApi.Types.TryGetValue(name, out var n))
            {
                Add("breaking", "removed", name, null, $"{o.Kind} {name} was removed or is no longer publicly visible.", o.Kind + " " + name, null);
                continue;
            }
            CompareType(o, n, Add);
        }
        foreach (var (name, n) in newApi.Types.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!oldApi.Types.ContainsKey(name))
                Add("compatible", "added", name, null, $"New {n.Kind} {name}.", null, n.Kind + " " + name);

        var ordered = changes
            .OrderBy(c => Array.IndexOf(Order, c.Severity)).ThenBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Member ?? "", StringComparer.Ordinal).ToList();
        int breaking = ordered.Count(c => c.Severity == "breaking"), compatible = ordered.Count(c => c.Severity == "compatible");
        return new ApiDiffResult
        {
            OldName = oldApi.AssemblyName, OldVersion = oldApi.Version, NewName = newApi.AssemblyName, NewVersion = newApi.Version,
            OldTypes = oldApi.Types.Count, NewTypes = newApi.Types.Count,
            Breaking = breaking, Compatible = compatible, Info = ordered.Count - breaking - compatible,
            Changes = ordered,
            SuggestedBump = breaking > 0 ? "major" : compatible > 0 ? "minor" : "patch"
        };
    }

    private static void CompareType(ApiType o, ApiType n, Action<string, string, string, string?, string, string?, string?> add)
    {
        string t = o.FullName;
        if (o.Kind != n.Kind) add("breaking", "changed", t, null, $"{t} changed from {o.Kind} to {n.Kind}.", o.Kind, n.Kind);
        if (o.BaseType != n.BaseType)
            add("breaking", "changed", t, null, $"Base type of {t} changed from {o.BaseType ?? "none"} to {n.BaseType ?? "none"}; code that relied on the old base type or its members can break.", o.BaseType, n.BaseType);
        if (!o.IsSealed && n.IsSealed && n.Kind == "class") add("breaking", "changed", t, null, $"{t} is now sealed: it can no longer be derived from.", "not sealed", "sealed");
        if (o.IsSealed && !n.IsSealed && n.Kind == "class") add("compatible", "changed", t, null, $"{t} is no longer sealed.", "sealed", "not sealed");
        if (!o.IsAbstract && n.IsAbstract && n.Kind == "class") add("breaking", "changed", t, null, $"{t} is now abstract: it can no longer be created with new.", "concrete", "abstract");
        if (o.IsAbstract && !n.IsAbstract && n.Kind == "class") add("compatible", "changed", t, null, $"{t} is no longer abstract.", "abstract", "concrete");
        if (o.Access != n.Access)
            add(Rank(n.Access) < Rank(o.Access) ? "breaking" : "compatible", "changed", t, null, $"Visibility of {t} changed from {o.Access} to {n.Access}.", o.Access, n.Access);
        if (!o.IsObsolete && n.IsObsolete) add("info", "changed", t, null, $"{t} is now marked [Obsolete].", null, "[Obsolete]");

        foreach (var i in o.Interfaces.Except(n.Interfaces)) add("breaking", "removed", t, null, $"{t} no longer implements {i}.", i, null);
        foreach (var i in n.Interfaces.Except(o.Interfaces)) add("compatible", "added", t, null, $"{t} now implements {i}.", null, i);

        // members: match by identity; pair a lone removed + lone added method of the same name as one signature change
        var removed = o.Members.Values.Where(m => !n.Members.ContainsKey(m.Key)).ToList();
        var added = n.Members.Values.Where(m => !o.Members.ContainsKey(m.Key)).ToList();
        foreach (var r in removed.ToList())
        {
            if (r.Kind is not ("method" or "constructor")) continue;
            var sameName = added.Where(a => a.Kind == r.Kind && NameOf(a) == NameOf(r)).ToList();
            var removedSameName = removed.Where(x => x.Kind == r.Kind && NameOf(x) == NameOf(r)).ToList();
            if (sameName.Count == 1 && removedSameName.Count == 1)
            {
                var a = sameName[0];
                add("breaking", "changed", t, NameOf(r), $"Signature of {t}.{NameOf(r)} changed: callers using the old overload no longer compile.", r.Display, a.Display);
                removed.Remove(r); added.Remove(a);
            }
        }
        foreach (var m in removed) add("breaking", "removed", t, m.Display, $"{m.Kind} {t}.{m.Display} was removed.", m.Display, null);
        foreach (var m in added)
        {
            bool forcesImplementers = m.IsAbstract && !m.IsStatic && (n.Kind == "interface" || (!n.IsSealed && n.Kind == "class"));
            if (forcesImplementers)
                add("breaking", "added", t, m.Display, $"Abstract {m.Kind} {t}.{m.Display} was added: every existing {(n.Kind == "interface" ? "implementation" : "derived class")} must now provide it.", null, m.Display);
            else if (n.Kind == "enum")
                add("compatible", "added", t, m.Display, $"Enum value {t}.{m.Display} was added" + (m.Constant != null ? " = " + m.Constant : "") + ".", null, m.Constant);
            else
                add("compatible", "added", t, m.Display, $"New {m.Kind} {t}.{m.Display}.", null, m.Display);
        }

        foreach (var (key, om) in o.Members)
            if (n.Members.TryGetValue(key, out var nm)) CompareMember(t, om, nm, n.Kind, add);
    }

    private static void CompareMember(string t, ApiMember o, ApiMember n, string typeKind, Action<string, string, string, string?, string, string?, string?> add)
    {
        string who = $"{t}.{o.Display}";
        if (o.Returns != n.Returns && o.Returns.Length > 0)
            add("breaking", "changed", t, o.Display, $"Type of {who} changed from {o.Returns} to {n.Returns}.", o.Returns, n.Returns);
        if (o.Access != n.Access)
            add(Rank(n.Access) < Rank(o.Access) ? "breaking" : "compatible", "changed", t, o.Display, $"Visibility of {who} changed from {o.Access} to {n.Access}.", o.Access, n.Access);
        if (o.IsStatic != n.IsStatic)
            add("breaking", "changed", t, o.Display, $"{who} changed from {(o.IsStatic ? "static to instance" : "instance to static")}.", o.IsStatic ? "static" : "instance", n.IsStatic ? "static" : "instance");
        if (o.IsAbstract != n.IsAbstract)
            add(n.IsAbstract ? "breaking" : "compatible", "changed", t, o.Display, n.IsAbstract ? $"{who} is now abstract: derived classes must override it." : $"{who} is no longer abstract.", o.IsAbstract ? "abstract" : "concrete", n.IsAbstract ? "abstract" : "concrete");
        if (o.IsVirtual && !n.IsVirtual && !n.IsAbstract && !n.IsSealed)
            add("breaking", "changed", t, o.Display, $"{who} is no longer virtual: existing overrides in derived classes stop compiling.", "virtual", "non-virtual");
        if (!o.IsVirtual && n.IsVirtual && !o.IsAbstract)
            add("info", "changed", t, o.Display, $"{who} is now virtual.", "non-virtual", "virtual");
        if (!o.IsSealed && n.IsSealed)
            add("breaking", "changed", t, o.Display, $"{who} is now sealed: it can no longer be overridden.", "overridable", "sealed");
        if (o.Constant != n.Constant && o.Kind == "field")
            add("breaking", "changed", t, o.Display, typeKind == "enum" ? $"Value of {who} changed from {o.Constant} to {n.Constant}." : $"Constant {who} changed from {o.Constant} to {n.Constant}: code compiled against the old version keeps the old value.", o.Constant, n.Constant);
        if (o.Kind == "property")
        {
            if (o.HasGetter && !n.HasGetter) add("breaking", "removed", t, o.Display, $"{who} lost its getter.", "get", null);
            if (o.HasSetter && !n.HasSetter) add("breaking", "removed", t, o.Display, $"{who} lost its setter.", "set", null);
            if (!o.HasGetter && n.HasGetter) add("compatible", "added", t, o.Display, $"{who} gained a getter.", null, "get");
            if (!o.HasSetter && n.HasSetter) add("compatible", "added", t, o.Display, $"{who} gained a setter.", null, "set");
        }
        if (o.ParameterNames.Length == n.ParameterNames.Length && !o.ParameterNames.SequenceEqual(n.ParameterNames))
            add("info", "changed", t, o.Display, $"Parameter names of {who} changed (callers using named arguments no longer compile).", string.Join(", ", o.ParameterNames), string.Join(", ", n.ParameterNames));
        if (!o.IsObsolete && n.IsObsolete) add("info", "changed", t, o.Display, $"{who} is now marked [Obsolete].", null, "[Obsolete]");
        if (o.IsObsolete && !n.IsObsolete) add("info", "changed", t, o.Display, $"{who} is no longer marked [Obsolete].", "[Obsolete]", null);
    }

    private static string NameOf(ApiMember m) => m.Display.Substring(0, Math.Max(0, m.Display.IndexOf('(')));

    // widest first
    private static int Rank(string access) => access switch { "public" => 3, "protected internal" => 2, "protected" => 1, _ => 0 };
}
