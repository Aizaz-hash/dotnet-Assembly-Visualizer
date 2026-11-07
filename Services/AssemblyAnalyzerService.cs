using DotNetVisualizer.Web.Interfaces;
using static DotNetVisualizer.Core.IlReader;
using static DotNetVisualizer.Core.MetadataNames;
using DotNetVisualizer.Core.Vulnerabilities;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetVisualizer.Web.Services;

public class AssemblyAnalyzerService : IAssemblyAnalyzer
{
    private readonly ICircularDependencyDetector _detector;

    public AssemblyAnalyzerService(ICircularDependencyDetector detector)
    {
        _detector = detector;
    }


    public object Analyze(IFormFile file)
    {
        using var upload = file.OpenReadStream();
        return Analyze(upload);
    }

    // The .exe that sits next to a .NET Core / .NET 5+ application is a small native program that starts the runtime (the "apphost"): the code is in the
    // .dll beside it. It always references the host resolver (hostfxr), which a general native file does not.
    private static bool LooksLikeDotNetLauncher(Stream upload)
    {
        if (!upload.CanSeek) return false;
        try
        {
            upload.Position = 0;
            var buffer = new byte[(int)Math.Min(upload.Length, 4 * 1024 * 1024)];
            int read = 0, n;
            while (read < buffer.Length && (n = upload.Read(buffer, read, buffer.Length - read)) > 0) read += n;
            return buffer.AsSpan(0, read).IndexOf("hostfxr"u8) >= 0;
        }
        catch (IOException) { return false; }
    }

    public object Analyze(Stream upload)
    {
        var timer = new PipelineTimer();

        using var peReader = new PEReader(upload);

        if (!peReader.HasMetadata)
            throw new InvalidDataException(LooksLikeDotNetLauncher(upload)
                ? "This is the native launcher that \"dotnet build\" and \"dotnet publish\" put next to a .NET application, not the .NET code itself. Open the .dll with the same name from the same folder instead."
                : "This file is not a .NET assembly: it has no .NET metadata (a native / unmanaged library or another kind of file).");

        timer.Mark("load PE image");

        MetadataReader mdReader = peReader.GetMetadataReader();
        timer.Mark("read metadata tables");

        // Nothing the reader cannot read may stop the rest of the analysis, but nothing may be dropped silently either:
        // every skipped item is counted and reported (diagnostics), so a damaged file never looks like a clean one.
        var graph = CallGraphReader.Read(peReader, mdReader, timer.Mark);
        CollectStaticInitReferences(peReader, graph.StaticInitBodies, graph.DelegateTargets);

        var callPairs = graph.CallPairs;
        var methodAdjacencyList = graph.BuildAdjacencyList();

        // DETECT CIRCULAR DEPENDENCIES
        timer.Mark("graph construction");
        List<List<string>> detectedDeadlocks = _detector.DetectCycles(methodAdjacencyList);
        timer.Mark("cycle detection");

        var references = AssemblyReferenceReader.Read(mdReader);
        timer.Mark("assembly references");

        // RETURN ANALYSIS RESULT
        return new
        {
            classes = graph.ClassList,

            inheritance = new
            {
                nodes = graph.InheritanceNodes.Select(n => new { id = n.Id, name = n.Name, type = n.Type, @namespace = n.Namespace, nested = n.Nested, token = n.Token }).ToList(),
                edges = graph.InheritanceEdges.Select(e => new { from = e.From, to = e.To, relation = e.Relation }).ToList()
            },

            methodGraph = new
            {
                nodes = graph.MethodNodes.Select(n => new { id = n.Id, name = n.Name, parentClass = n.ParentClass, token = n.Token }).ToList(),
                edges = graph.MethodEdges.Select(e => new { from = e.From, to = e.To, relation = e.Relation, fromClass = e.FromClass, toClass = e.ToClass }).ToList()
            },

            deadlocks = detectedDeadlocks,

            // Assembly references (name + assembly version); shown by the vulnerability view as an inventory.
            references,
            assemblyName = AssemblyReferenceReader.ReadAssemblyName(mdReader),
            targetFramework = AssemblyReferenceReader.ReadOrInferTargetFramework(mdReader, out _),

            // what the assembly is, and whether it looks obfuscated (indicators with their evidence, never a verdict)
            assembly = AssemblyInsights.ReadInfo(peReader, mdReader, upload.CanSeek ? upload.Length : 0, references.Count),
            obfuscation = AssemblyInsights.DetectObfuscation(mdReader),

            metrics = BuildMetrics(graph.MethodDisplay, graph.MethodMetrics, callPairs),

            deadCode = FindDeadCode(graph.MethodMeta, graph.MethodMetrics, callPairs, graph.DelegateTargets, peReader),

            riskyApis = graph.RiskHits
                .OrderBy(h => Array.IndexOf(new[] { "High", "Medium", "Low" }, h.Key.Rule.Severity))
                .ThenBy(h => h.Key.Rule.Category, StringComparer.Ordinal)
                .ThenByDescending(h => h.Value.Count)
                .Select(h => new
                {
                    ruleId = h.Key.Rule.Id,
                    category = h.Key.Rule.Category,
                    severity = h.Key.Rule.Severity,
                    title = h.Key.Rule.Title,
                    api = h.Key.Api,
                    why = h.Key.Rule.Why,
                    advice = h.Key.Rule.Advice,
                    count = h.Value.Count,
                    callers = h.Value.Callers
                })
                .ToList(),

            diagnostics = new
            {
                referenceAssembly = AssemblyReferenceReader.IsReferenceAssembly(mdReader),
                skippedTypes = graph.Skipped.Types, skippedMethods = graph.Skipped.Methods, skippedInterfaces = graph.Skipped.Interfaces, unreadableBodies = graph.Skipped.Bodies,
                // true only when every type, member and method body could be read
                complete = graph.Skipped.Complete
            },

            timings = timer.ToResult()
        };
    }

    private static object BuildMetrics(Dictionary<string, (string Name, string Class)> names, Dictionary<string, (int Complexity, int IlSize)> metrics, List<(string From, string To)> calls)
    {
        var fanOut = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fanIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in calls)
        {
            fanOut[from] = fanOut.GetValueOrDefault(from) + 1;
            fanIn[to] = fanIn.GetValueOrDefault(to) + 1;
        }

        object Row(string id) => new
        {
            id,
            name = names.TryGetValue(id, out var n) ? n.Name : id,
            parentClass = names.TryGetValue(id, out n) ? n.Class : "",
            complexity = metrics.TryGetValue(id, out var m) ? m.Complexity : 0,
            ilSize = metrics.TryGetValue(id, out m) ? m.IlSize : 0,
            callers = fanIn.GetValueOrDefault(id),
            callees = fanOut.GetValueOrDefault(id)
        };

        var withBody = metrics.Where(kv => names.ContainsKey(kv.Key)).ToList();
        var buckets = new (string Label, int Min, int Max)[] { ("1-5", 1, 5), ("6-10", 6, 10), ("11-20", 11, 20), ("21-50", 21, 50), ("over 50", 51, int.MaxValue) };

        return new
        {
            methods = withBody.Count,
            totalIlBytes = withBody.Sum(kv => (long)kv.Value.IlSize),
            averageComplexity = withBody.Count == 0 ? 0 : Math.Round(withBody.Average(kv => (double)kv.Value.Complexity), 2),
            maxComplexity = withBody.Count == 0 ? 0 : withBody.Max(kv => kv.Value.Complexity),
            histogram = buckets.Select(b => new { label = b.Label, count = withBody.Count(kv => kv.Value.Complexity >= b.Min && kv.Value.Complexity <= b.Max) }).ToList(),
            hotspots = withBody.OrderByDescending(kv => kv.Value.Complexity).ThenByDescending(kv => kv.Value.IlSize).Take(100).Select(kv => Row(kv.Key)).ToList(),
            mostCalled = fanIn.Where(kv => names.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(50).Select(kv => Row(kv.Key)).ToList(),
            mostCalling = fanOut.Where(kv => names.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(50).Select(kv => Row(kv.Key)).ToList(),
            types = withBody.GroupBy(kv => names[kv.Key].Class)
                .Select(g => new { name = g.Key, methods = g.Count(), complexity = g.Sum(kv => kv.Value.Complexity), ilSize = g.Sum(kv => (long)kv.Value.IlSize) })
                .OrderByDescending(t => t.complexity).Take(50).ToList()
        };
    }

    /// <summary>Everything a static constructor (static field initializers, delegate tables, lazy singletons) calls or points at is referenced.</summary>
    private static void CollectStaticInitReferences(PEReader pe, List<int> rvas, HashSet<int> referenced)
    {
        foreach (int rva in rvas)
        {
            try
            {
                var il = pe.GetMethodBody(rva).GetILReader();
                while (TryReadInstruction(ref il, out OpCode op, out int token))
                {
                    if (token <= 0) continue;
                    if (op.Value == OpCodes.Call.Value || op.Value == OpCodes.Callvirt.Value || op.Value == OpCodes.Newobj.Value ||
                        op.Value == OpCodes.Ldftn.Value || op.Value == OpCodes.Ldvirtftn.Value)
                        referenced.Add(token);
                }
            }
            catch { /* unreadable body: nothing to add */ }
        }
    }

    private const int MaxDeadCode = 1000;

    /// <summary>
    /// Private / internal methods that nothing in this assembly calls or takes the address of. Conservative on purpose: virtual
    /// methods (overrides, interface implementations), constructors, entry points and methods with attributes are never listed,
    /// and a method that is only called by reflection, serialization or another assembly (InternalsVisibleTo) can still be
    /// reported - hence "unreferenced", not "unused".
    /// </summary>
    private static List<object> FindDeadCode(
        Dictionary<string, (MethodDefinitionHandle Handle, MethodAttributes Attributes, bool HasAttributes, string Name, string ParentClass)> meta,
        Dictionary<string, (int Complexity, int IlSize)> metrics,
        List<(string From, string To)> calls, HashSet<int> delegateTargets, PEReader pe)
    {
        var called = new HashSet<string>(calls.Select(c => c.To), StringComparer.OrdinalIgnoreCase);
        int entryToken = 0;
        try { entryToken = pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0; } catch { /* no entry point */ }

        var result = new List<object>();
        foreach (var (id, m) in meta.OrderBy(kv => kv.Value.ParentClass, StringComparer.Ordinal).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var access = m.Attributes & MethodAttributes.MemberAccessMask;
            if (access != MethodAttributes.Private && access != MethodAttributes.Assembly && access != MethodAttributes.FamANDAssem) continue;
            if ((m.Attributes & (MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0) continue;
            if (m.HasAttributes) continue;
            if (m.Name.StartsWith(".") || m.Name.StartsWith("<")) continue;                       // constructors, compiler generated
            int token = MetadataTokens.GetToken(m.Handle);
            if (token == entryToken) continue;
            if (called.Contains(id) || delegateTargets.Contains(token)) continue;
            if (!metrics.ContainsKey(id)) continue;                                              // no body to run

            result.Add(new
            {
                id, name = id.StartsWith(m.ParentClass + ".", StringComparison.Ordinal) ? id.Substring(m.ParentClass.Length + 1) : id, parentClass = m.ParentClass,
                access = access == MethodAttributes.Private ? "private" : "internal",
                ilSize = metrics[id].IlSize, complexity = metrics[id].Complexity
            });
            if (result.Count >= MaxDeadCode) break;
        }
        return result;
    }
}
