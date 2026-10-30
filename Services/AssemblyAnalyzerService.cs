using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.Services.Vulnerabilities;
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

    private static readonly Dictionary<short, OpCode> IlOpCodes = CreateIlOpCodeMap();

    public object Analyze(IFormFile file)
    {
        using var upload = file.OpenReadStream();
        return Analyze(upload);
    }

    public object Analyze(Stream upload)
    {
        var timer = new PipelineTimer();
        // Nothing the analyzer cannot read may stop the rest of the analysis, but nothing may be dropped silently either:
        // every skipped item is counted and reported (diagnostics), so a damaged file never looks like a clean one.
        int skippedTypes = 0, skippedMethods = 0, skippedInterfaces = 0, unreadableBodies = 0;
        var inheritanceNodes = new List<object>();
        var inheritanceEdges = new List<object>();
        var classList = new List<string>();

        var methodNodes = new List<object>();
        // parent class -> ids of its method nodes in registration order (replaces a linear scan over every node)
        var methodIdsByClass = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var methodEdges = new List<object>();
        var callPairs = new List<(string From, string To)>();
        // Constructors are only listed when something calls them or they call something: a compiler-generated default
        // constructor that just calls object() would otherwise add a meaningless node to every class.
        var pendingConstructors = new List<(string Id, object Node, string Name, string Class)>();
        var methodDisplay = new Dictionary<string, (string Name, string Class)>(StringComparer.OrdinalIgnoreCase);   // id -> label and owning class, for the metrics
        // Calls to APIs that deserve a security look (see RiskyApiRules): rule + api -> how often and from where.
        var riskHits = new Dictionary<(RiskyApiRule Rule, string Api), RiskHit>();
        var riskByToken = new Dictionary<int, (RiskyApiRule Rule, string Api)?>();
        // Code metrics: cyclomatic complexity and IL size per method, and what dead-code detection needs.
        var methodMetrics = new Dictionary<string, (int Complexity, int IlSize)>(StringComparer.OrdinalIgnoreCase);
        var methodMeta = new Dictionary<string, (MethodDefinitionHandle Handle, MethodAttributes Attributes, bool HasAttributes, string Name, string ParentClass)>(StringComparer.OrdinalIgnoreCase);
        var delegateTargets = new HashSet<int>();            // methods whose address is taken (ldftn / ldvirtftn): lambdas, method groups, events
        var staticInitBodies = new List<int>();              // static constructors are not graph nodes, but what they call / point to still counts as used
        var resolvedCallTargets = new Dictionary<int, (string Id, string ParentClass, bool IsInternal)>();

        var registeredMethodIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokenToUniqueMethodIdMap = new Dictionary<EntityHandle, string>();

        // Keep track of methods that actually have executable bodies to scan in pass 2
        var methodsToScan = new List<(string CallerMethodId, string CallerClass, int Rva)>();

        using var peReader = new PEReader(upload);

        if (!peReader.HasMetadata)
            throw new InvalidDataException("This file is not a .NET assembly: it has no .NET metadata (a native / unmanaged library or another kind of file).");

        timer.Mark("load PE image");

        MetadataReader mdReader = peReader.GetMetadataReader();
        timer.Mark("read metadata tables");

        var lambdaBindingNames = GetLambdaBindingNames(peReader, mdReader);
        timer.Mark("lambda name scan (IL)");

        // FIRST PASS: Map structures, classes, methods and metadata tokens.
        foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
        {
            try
            {
                TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);
                string name = mdReader.GetString(typeDef.Name);
                bool isTopLevelStatementsType = IsTopLevelStatementsType(mdReader, typeDef);
                bool hasContainingSourceType = TryGetContainingSourceTypeName(mdReader, typeDef, out string lambdaContainingTypeName);
                bool isLambdaGeneratedType = IsLambdaGeneratedType(name) && hasContainingSourceType;

                if ((IsCompilerGeneratedName(name) && !isTopLevelStatementsType && !isLambdaGeneratedType) || name.Equals("<Module>"))
                    continue;

                string fullName = isLambdaGeneratedType ? lambdaContainingTypeName : GetTypeDefinitionFullName(mdReader, typeHandle);

                bool isInterface = (typeDef.Attributes & TypeAttributes.Interface) != 0;

                if (!isLambdaGeneratedType)
                {
                    classList.Add(fullName);

                    inheritanceNodes.Add(new
                    {
                        id = fullName,
                        name = GetDisplayTypeName(mdReader, typeDef),
                        type = isInterface ? "Interface" : "Class",
                        // the real namespace (of the outermost type): a nested type's full name also contains dots, so the browser
                        // cannot recover the namespace from the name without inventing one per enclosing type
                        @namespace = GetOutermostNamespace(mdReader, typeDef),
                        nested = !typeDef.GetDeclaringType().IsNil,
                        token = MetadataTokens.GetToken(typeHandle)          // lets the C# view ask for this type's source
                    });
                }

                // Base inheritance
                if (!typeDef.BaseType.IsNil)
                {
                    string baseName = GetStringFromEntityHandle(mdReader, typeDef.BaseType);

                    if (!string.IsNullOrEmpty(baseName) && !baseName.Equals("System.Object"))
                    {
                        inheritanceEdges.Add(new
                        {
                            from = fullName,
                            to = baseName,
                            relation = "Inherits"
                        });
                    }
                }

                // Interface implementations
                foreach (InterfaceImplementationHandle ifaceHandle in typeDef.GetInterfaceImplementations())
                {
                    try
                    {
                        InterfaceImplementation ifaceImpl = mdReader.GetInterfaceImplementation(ifaceHandle);

                        string ifaceName = GetStringFromEntityHandle(mdReader, ifaceImpl.Interface);

                        if (!string.IsNullOrEmpty(ifaceName))
                        {
                            inheritanceEdges.Add(new
                            {
                                from = fullName,
                                to = ifaceName,
                                relation = "Implements"
                            });
                        }
                    }
                    catch
                    {
                        skippedInterfaces++;
                    }
                }

                if (isInterface)
                    continue;

                // Register methods
                foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
                {
                    try
                    {
                        MethodDefinition methodDef = mdReader.GetMethodDefinition(methodHandle);

                        string methodName = mdReader.GetString(methodDef.Name);

                        bool isTopLevelMain = methodName.Equals("<Main>$");

                        string lambdaDisplayName = isLambdaGeneratedType ? GetLambdaDisplayName(name, methodName, lambdaBindingNames) : string.Empty;

                        bool isLambdaBody = !string.IsNullOrEmpty(lambdaDisplayName) && (methodName.Contains("b__") || methodName.Equals("MoveNext"));

                        if (methodName.Equals(".cctor") && !isLambdaGeneratedType && methodDef.RelativeVirtualAddress > 0)
                            staticInitBodies.Add(methodDef.RelativeVirtualAddress);

                        // Instance constructors take part in the call graph (new Foo(...) is a newobj, base(...) a call to .ctor).
                        // Static constructors and constructors of compiler-generated lambda types are still left out.
                        bool isConstructor = methodName.Equals(".ctor") && !isLambdaGeneratedType;

                        if ((isLambdaGeneratedType && !isLambdaBody) || (!isLambdaGeneratedType && methodName.StartsWith("<") && !isTopLevelMain) || (methodName.Equals(".ctor") && !isConstructor) || methodName.Equals(".cctor"))
                            continue;

                        string displayMethodName = isTopLevelMain ? "Main" : isLambdaBody ? lambdaDisplayName : methodName;

                        string methodSignature = GetMethodSignature(mdReader, methodHandle, displayMethodName);

                        string baseMethodId = $"{fullName}.{methodSignature}";

                        string methodUniqueId = baseMethodId;
                        int duplicateIndex = 1;

                        while (registeredMethodIds.Contains(methodUniqueId) && !isLambdaBody)
                        {
                            methodUniqueId = $"{fullName}.{GetMethodNameWithDuplicateSuffix(methodSignature, duplicateIndex)}";
                            duplicateIndex++;
                        }

                        bool isNewMethodNode = registeredMethodIds.Add(methodUniqueId);

                        // Map metadata method token -> unique visualizer ID
                        tokenToUniqueMethodIdMap[methodHandle] = methodUniqueId;

                        if (!isLambdaBody && !isTopLevelMain && !isLambdaGeneratedType)
                            methodMeta[methodUniqueId] = (methodHandle, methodDef.Attributes, methodDef.GetCustomAttributes().Count > 0, methodName, fullName);

                        if ((methodDef.Attributes & MethodAttributes.PinvokeImpl) != 0)
                        {
                            var import = methodDef.GetImport();
                            if (!import.Module.IsNil)
                                RecordRisk(riskHits, (RiskyApiRules.NativeCall, mdReader.GetString(mdReader.GetModuleReference(import.Module).Name) + "!" + mdReader.GetString(import.Name)), methodUniqueId);
                        }

                        if (isNewMethodNode && isConstructor)
                        {
                            // shown as "Foo(int a)" like ILSpy; the id keeps ".ctor" so it matches member references
                            string ctorLabel = GetDisplayTypeName(mdReader, typeDef) + methodSignature.Substring(methodSignature.IndexOf('('));
                            pendingConstructors.Add((methodUniqueId, new { id = methodUniqueId, name = ctorLabel, parentClass = fullName, token = MetadataTokens.GetToken(methodHandle) }, ctorLabel, fullName));
                        }
                        else if (isNewMethodNode)
                        {
                            if (!methodIdsByClass.TryGetValue(fullName, out var idsOfClass))
                                methodIdsByClass[fullName] = idsOfClass = new List<string>();
                            idsOfClass.Add(methodUniqueId);

                            methodDisplay[methodUniqueId] = (methodSignature, fullName);
                            methodNodes.Add(new
                            {
                                id = methodUniqueId,
                                name = methodSignature,
                                parentClass = fullName,
                                token = MetadataTokens.GetToken(methodHandle)
                            });
                        }

                        // Queue methods containing executable IL for pass 2.
                        if (methodDef.RelativeVirtualAddress > 0)
                        {
                            methodsToScan.Add((
                                methodUniqueId,
                                fullName,
                                methodDef.RelativeVirtualAddress
                            ));
                        }
                    }
                    catch
                    {
                        skippedMethods++;
                    }
                }
            }
            catch
            {
                skippedTypes++;
            }
        }

        timer.Mark("type + method discovery");

        // PREPARE LOOKUPS
        var classSet = new HashSet<string>(classList, StringComparer.OrdinalIgnoreCase);

        // Deduplicate caller -> callee edges.
        var edgeKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // IMPORTANT:
        // TryReadInstruction() advances the IL reader over the complete
        // instruction, including its operand. This prevents the reader from
        // becoming misaligned after instructions such as: ldfld, ldstr, ldc.i4, br, ldarg, newobj, etc.
        foreach (var item in methodsToScan)
        {
            try
            {
                MethodBodyBlock body = peReader.GetMethodBody(item.Rva);

                BlobReader ilReader = body.GetILReader();
                int ilSize = ilReader.Length;
                int complexity = 1 + body.ExceptionRegions.Count(r => r.Kind == ExceptionRegionKind.Catch || r.Kind == ExceptionRegionKind.Filter);

                while (TryReadInstruction(ref ilReader, out OpCode opCode, out int metadataToken))
                {
                    if (opCode.OperandType == OperandType.InlineSwitch) complexity += -metadataToken;
                    else if (opCode.FlowControl == FlowControl.Cond_Branch) complexity++;
                    else if ((opCode.Value == OpCodes.Ldftn.Value || opCode.Value == OpCodes.Ldvirtftn.Value) && metadataToken > 0) delegateTargets.Add(metadataToken);

                    // We only care about actual method calls.
                    if (opCode.Value != OpCodes.Call.Value && opCode.Value != OpCodes.Callvirt.Value && opCode.Value != OpCodes.Newobj.Value)
                    {
                        continue;
                    }

                    if (metadataToken == 0)
                        continue;

                    EntityHandle calledEntityHandle = MetadataTokens.EntityHandle(metadataToken);

                    if (!riskByToken.TryGetValue(metadataToken, out var risk))
                        riskByToken[metadataToken] = risk = FindRisk(mdReader, calledEntityHandle);
                    if (risk != null) RecordRisk(riskHits, risk.Value, item.CallerMethodId);

                    // The same callee is referenced from many call sites; resolve each metadata token only once.
                    bool alreadyResolved = resolvedCallTargets.TryGetValue(metadataToken, out var resolved);
                    string calleeMethodId = alreadyResolved ? resolved.Id : string.Empty;
                    string calleeParentClass = alreadyResolved ? resolved.ParentClass : string.Empty;
                    bool isInternalTarget = alreadyResolved && resolved.IsInternal;

                    if (!alreadyResolved)
                    {

                    // Direct MethodDefinition
                    if (calledEntityHandle.Kind == HandleKind.MethodDefinition)
                    {
                        if (tokenToUniqueMethodIdMap.TryGetValue(calledEntityHandle, out string? mappedMethodId))
                        {
                            calleeMethodId = mappedMethodId;

                            MethodDefinition calledMethod = mdReader.GetMethodDefinition((MethodDefinitionHandle)calledEntityHandle);

                            TypeDefinitionHandle declaringType = calledMethod.GetDeclaringType();

                            if (!declaringType.IsNil)
                                calleeParentClass = GetTypeDefinitionFullName(mdReader, declaringType);

                            isInternalTarget = true;
                        }
                    }
                    // MemberReference
                    else if (calledEntityHandle.Kind == HandleKind.MemberReference)
                    {
                        MemberReference memRef = mdReader.GetMemberReference((MemberReferenceHandle)calledEntityHandle);

                        if (memRef.Parent.Kind == HandleKind.TypeReference || memRef.Parent.Kind == HandleKind.TypeDefinition)
                        {
                            calleeParentClass = GetStringFromEntityHandle(mdReader, memRef.Parent);
                            string targetMethodName = mdReader.GetString(memRef.Name);

                            if (!targetMethodName.StartsWith("<") && !targetMethodName.Equals(".ctor"))
                            {
                                string targetSignature = GetMemberReferenceMethodSignature(mdReader, (MemberReferenceHandle)calledEntityHandle, targetMethodName);

                                calleeMethodId = $"{calleeParentClass}.{targetSignature}";

                                isInternalTarget = classSet.Contains(calleeParentClass);

                                // If we can find the exact method in the
                                // analyzed assembly, use its canonical ID.
                                string? canonicalMethodId = FindCanonicalMethodId(methodIdsByClass, calleeParentClass, targetSignature);

                                if (!string.IsNullOrEmpty(canonicalMethodId))
                                    calleeMethodId = canonicalMethodId;
                            }
                        }
                    }
                    // MethodSpecification
                    // Handles generic method calls.
                    else if (calledEntityHandle.Kind == HandleKind.MethodSpecification)
                    {
                        MethodSpecification methodSpec = mdReader.GetMethodSpecification((MethodSpecificationHandle)calledEntityHandle);

                        EntityHandle underlyingMethod = methodSpec.Method;

                        // Generic method whose underlying method is a MethodDefinition.
                        if (underlyingMethod.Kind == HandleKind.MethodDefinition)
                        {
                            if (tokenToUniqueMethodIdMap.TryGetValue(underlyingMethod, out string? mappedMethodId))
                            {
                                calleeMethodId = mappedMethodId;

                                MethodDefinition calledMethod = mdReader.GetMethodDefinition((MethodDefinitionHandle)underlyingMethod);

                                TypeDefinitionHandle declaringType = calledMethod.GetDeclaringType();

                                if (!declaringType.IsNil)
                                    calleeParentClass = GetTypeDefinitionFullName(mdReader, declaringType);

                                isInternalTarget = true;
                            }
                        }
                        // Generic method whose underlying method is a MemberReference.
                        else if (underlyingMethod.Kind == HandleKind.MemberReference)
                        {
                            MemberReference memRef = mdReader.GetMemberReference((MemberReferenceHandle)underlyingMethod);

                            if (memRef.Parent.Kind == HandleKind.TypeReference || memRef.Parent.Kind == HandleKind.TypeDefinition)
                            {
                                calleeParentClass = GetStringFromEntityHandle(mdReader, memRef.Parent);

                                string targetMethodName = mdReader.GetString(memRef.Name);

                                if (!targetMethodName.StartsWith("<") && !targetMethodName.Equals(".ctor"))
                                {
                                    string targetSignature = GetMemberReferenceMethodSignature(mdReader, (MemberReferenceHandle)underlyingMethod, targetMethodName);

                                    calleeMethodId = $"{calleeParentClass}.{targetSignature}";

                                    isInternalTarget = classSet.Contains(calleeParentClass);

                                    string? canonicalMethodId = FindCanonicalMethodId(methodIdsByClass, calleeParentClass, targetSignature);

                                    if (!string.IsNullOrEmpty(canonicalMethodId))
                                    {
                                        calleeMethodId = canonicalMethodId;
                                    }
                                }
                            }
                        }
                    }

                    resolvedCallTargets[metadataToken] = (calleeMethodId, calleeParentClass, isInternalTarget);
                    }

                    // Register valid internal call
                    if (!string.IsNullOrEmpty(calleeMethodId) && !calleeMethodId.Equals(item.CallerMethodId, StringComparison.OrdinalIgnoreCase) && isInternalTarget)
                    {
                        string edgeKey = $"{item.CallerMethodId}|{calleeMethodId}";

                        if (edgeKeySet.Add(edgeKey))
                        {
                            callPairs.Add((item.CallerMethodId, calleeMethodId));
                            methodEdges.Add(new
                            {
                                from = item.CallerMethodId,
                                to = calleeMethodId,
                                relation = "Calls",
                                fromClass = item.CallerClass,
                                toClass = calleeParentClass
                            });
                        }
                    }
                }

                methodMetrics[item.CallerMethodId] = (complexity, ilSize);
            }
            catch
            {
                unreadableBodies++;
            }
        }

        CollectStaticInitReferences(peReader, staticInitBodies, delegateTargets);

        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in callPairs) { connected.Add(from); connected.Add(to); }
        foreach (var (id, node, ctorName, ctorClass) in pendingConstructors)
            if (connected.Contains(id)) { methodNodes.Add(node); methodDisplay[id] = (ctorName, ctorClass); }

        timer.Mark("call analysis (IL scan)");

        // BUILD METHOD ADJACENCY LIST
        var methodAdjacencyList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fromNode, toNode) in callPairs)
        {
            if (!methodAdjacencyList.TryGetValue(fromNode, out var callees))
                methodAdjacencyList[fromNode] = callees = new List<string>();

            callees.Add(toNode);
        }

        // DETECT CIRCULAR DEPENDENCIES
        timer.Mark("graph construction");
        List<List<string>> detectedDeadlocks = _detector.DetectCycles(methodAdjacencyList);
        timer.Mark("cycle detection");

        var references = AssemblyReferenceReader.Read(mdReader);
        timer.Mark("assembly references");

        // RETURN ANALYSIS RESULT
        return new
        {
            classes = classList,

            inheritance = new
            {
                nodes = inheritanceNodes,
                edges = inheritanceEdges
            },

            methodGraph = new
            {
                nodes = methodNodes,
                edges = methodEdges
            },

            deadlocks = detectedDeadlocks,

            // Assembly references (name + assembly version); shown by the vulnerability view as an inventory.
            references,
            assemblyName = AssemblyReferenceReader.ReadAssemblyName(mdReader),
            targetFramework = AssemblyReferenceReader.ReadTargetFramework(mdReader),

            // what the assembly is, and whether it looks obfuscated (indicators with their evidence, never a verdict)
            assembly = AssemblyInsights.ReadInfo(peReader, mdReader, upload.CanSeek ? upload.Length : 0, AssemblyReferenceReader.ReadTargetFramework(mdReader), references.Count),
            obfuscation = AssemblyInsights.DetectObfuscation(mdReader),

            metrics = BuildMetrics(methodDisplay, methodMetrics, callPairs),

            deadCode = FindDeadCode(methodMeta, methodMetrics, callPairs, delegateTargets, peReader),

            riskyApis = riskHits
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
                skippedTypes, skippedMethods, skippedInterfaces, unreadableBodies,
                // true only when every type, member and method body could be read
                complete = skippedTypes + skippedMethods + skippedInterfaces + unreadableBodies == 0
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

    private sealed class RiskHit
    {
        public int Count;
        public List<string> Callers { get; } = new();
    }

    private const int MaxRiskCallers = 25;

    private static void RecordRisk(Dictionary<(RiskyApiRule Rule, string Api), RiskHit> hits, (RiskyApiRule Rule, string Api) risk, string callerMethodId)
    {
        if (!hits.TryGetValue(risk, out var hit)) hits[risk] = hit = new RiskHit();
        hit.Count++;
        if (hit.Callers.Count < MaxRiskCallers && !hit.Callers.Contains(callerMethodId)) hit.Callers.Add(callerMethodId);
    }

    /// <summary>Is this call target one of the APIs in <see cref="RiskyApiRules"/>? Only references to other assemblies can match.</summary>
    private static (RiskyApiRule Rule, string Api)? FindRisk(MetadataReader reader, EntityHandle target)
    {
        try
        {
            if (target.Kind == HandleKind.MethodSpecification)
                target = reader.GetMethodSpecification((MethodSpecificationHandle)target).Method;

            if (target.Kind != HandleKind.MemberReference) return null;
            var member = reader.GetMemberReference((MemberReferenceHandle)target);
            if (member.Parent.Kind != HandleKind.TypeReference) return null;

            string type = GetStringFromEntityHandle(reader, member.Parent);
            string method = reader.GetString(member.Name);
            var rule = RiskyApiRules.Match(type, method);
            return rule == null ? null : (rule, method == ".ctor" ? "new " + type : type + "." + method);
        }
        catch
        {
            return null;
        }
    }

    // METHOD SIGNATURE HELPERS
    private static string GetMethodSignature(MetadataReader reader, MethodDefinitionHandle methodHandle, string displayMethodName)
    {
        MethodDefinition method = reader.GetMethodDefinition(methodHandle);

        try
        {
            var provider = new MethodSignatureTypeProvider(reader);

            var signature = method.DecodeSignature(provider, genericContext: null);

            var parameterNames = new List<string>();

            foreach (ParameterHandle parameterHandle in method.GetParameters())
            {
                Parameter parameter = reader.GetParameter(parameterHandle);

                if (parameter.SequenceNumber == 0)
                    continue;

                string parameterName = parameter.Name.IsNil ? $"arg{parameter.SequenceNumber - 1}" : reader.GetString(parameter.Name);

                parameterNames.Add(parameterName);
            }

            var parameters = new List<string>();

            for (int i = 0; i < signature.ParameterTypes.Length; i++)
            {
                string parameterType = signature.ParameterTypes[i];

                string parameterName = i < parameterNames.Count ? parameterNames[i] : $"arg{i}";

                parameters.Add($"{parameterType} {parameterName}");
            }

            return $"{displayMethodName}({string.Join(", ", parameters)})";
        }
        catch
        {
            // If signature decoding fails, retain the method name
            // rather than preventing the entire assembly from loading.
            return $"{displayMethodName}()";
        }
    }

    private static string GetMemberReferenceMethodSignature(MetadataReader reader, MemberReferenceHandle memberReferenceHandle, string displayMethodName)
    {
        try
        {
            MemberReference memberReference = reader.GetMemberReference(memberReferenceHandle);

            var provider = new MethodSignatureTypeProvider(reader);

            var signature = memberReference.DecodeMethodSignature(provider, genericContext: null);

            var parameters = new List<string>();

            for (int i = 0; i < signature.ParameterTypes.Length; i++)
            {
                parameters.Add($"{signature.ParameterTypes[i]} arg{i}");
            }

            return $"{displayMethodName}({string.Join(", ", parameters)})";
        }
        catch
        {
            return $"{displayMethodName}()";
        }
    }

    private static string GetMethodNameWithDuplicateSuffix(string methodSignature, int duplicateIndex)
    {
        int openingParenthesis = methodSignature.IndexOf('(');

        if (openingParenthesis < 0)
            return $"{methodSignature}_{duplicateIndex}()";

        string methodName = methodSignature.Substring(0, openingParenthesis);

        string parameters = methodSignature.Substring(openingParenthesis);

        return $"{methodName}_{duplicateIndex}{parameters}";
    }

private static string? FindCanonicalMethodId(Dictionary<string, List<string>> methodIdsByClass, string parentClass, string methodSignature)    {        if (!methodIdsByClass.TryGetValue(parentClass, out var ids))            return null;        string suffix = "." + methodSignature;        foreach (string id in ids)        {            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))                return id;        }        return null;    }

    // METADATA HELPERS
    private static string GetStringFromEntityHandle(MetadataReader reader, EntityHandle handle)
    {
        try
        {
            if (handle.IsNil)
                return string.Empty;

            if (handle.Kind == HandleKind.TypeDefinition)
            {
                return GetTypeDefinitionFullName(reader, (TypeDefinitionHandle)handle);
            }

            if (handle.Kind == HandleKind.TypeReference)
            {
                var tr = reader.GetTypeReference((TypeReferenceHandle)handle);

                string ns = reader.GetString(tr.Namespace);

                string name = reader.GetString(tr.Name);

                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }

            if (handle.Kind == HandleKind.TypeSpecification)
            {
                // A generic instantiation (Repository<User>, IComparable<Square>): name the generic type itself, so the edge
                // points at a real type instead of a placeholder. The type arguments are not part of the identity.
                string decoded = reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(new MethodSignatureTypeProvider(reader), null!);
                int open = decoded.IndexOf('<');
                return open > 0 ? decoded.Substring(0, open) : decoded;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsCompilerGeneratedName(string name)
    {
        return name.StartsWith("<");
    }

    private static bool IsLambdaGeneratedType(string name)
    {
        return name.StartsWith("<>c") || name.Contains("b__");
    }

    private static bool IsTopLevelStatementsType(MetadataReader reader, TypeDefinition typeDef)
    {
        if (!typeDef.GetDeclaringType().IsNil)
            return false;

        foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(methodHandle);

            if (reader.GetString(method.Name).Equals("<Main>$"))
                return true;
        }

        return false;
    }

    private static bool TryGetContainingSourceTypeName(MetadataReader reader, TypeDefinition typeDef, out string fullName)
    {
        TypeDefinitionHandle declaringTypeHandle = typeDef.GetDeclaringType();

        while (!declaringTypeHandle.IsNil)
        {
            TypeDefinition declaringType = reader.GetTypeDefinition(declaringTypeHandle);

            string declaringTypeName = reader.GetString(declaringType.Name);

            if (!IsCompilerGeneratedName(declaringTypeName) || IsTopLevelStatementsType(reader, declaringType))
            {
                fullName = GetTypeDefinitionFullName(reader, declaringTypeHandle);

                return true;
            }

            declaringTypeHandle = declaringType.GetDeclaringType();
        }

        fullName = string.Empty;
        return false;
    }

    private static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        var typeNames = new Stack<string>();

        TypeDefinitionHandle currentTypeHandle = typeHandle;

        while (!currentTypeHandle.IsNil)
        {
            TypeDefinition currentType = reader.GetTypeDefinition(currentTypeHandle);

            typeNames.Push(GetDisplayTypeName(reader, currentType));

            currentTypeHandle = currentType.GetDeclaringType();
        }

        TypeDefinition outerType = reader.GetTypeDefinition(typeHandle);

        while (!outerType.GetDeclaringType().IsNil)
            outerType = reader.GetTypeDefinition(outerType.GetDeclaringType());

        string ns = reader.GetString(outerType.Namespace);

        string typeName = string.Join(".", typeNames);

        return string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
    }

    private static string GetOutermostNamespace(MetadataReader reader, TypeDefinition typeDef)
    {
        while (!typeDef.GetDeclaringType().IsNil)
            typeDef = reader.GetTypeDefinition(typeDef.GetDeclaringType());
        return reader.GetString(typeDef.Namespace);
    }

    private static string GetDisplayTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        string name = reader.GetString(typeDef.Name);

        return IsTopLevelStatementsType(reader, typeDef) && name.StartsWith("<") && name.EndsWith(">$") ? name.Substring(1, name.Length - 3) : name;
    }

    // LAMBDA DETECTION
    private static Dictionary<string, string> GetLambdaBindingNames(PEReader peReader, MetadataReader reader)
    {
        var routeNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);

                if (method.RelativeVirtualAddress == 0)
                    continue;

                try
                {
                    string? lastString = null;
                    string? lastLambdaIdentifier = null;
                    bool hasDelegateInstance = false;
                    bool lastInstructionWasDup = false;

                    BlobReader ilReader = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader();

                    while (TryReadInstruction(ref ilReader, out OpCode opCode, out int metadataToken))
                    {
                        if (opCode.Value == OpCodes.Ldstr.Value)
                        {
                            lastString = reader.GetUserString(MetadataTokens.UserStringHandle(metadataToken));
                        }
                        else if (opCode.Value == OpCodes.Ldftn.Value || opCode.Value == OpCodes.Ldvirtftn.Value)
                        {
                            lastLambdaIdentifier = GetLambdaIdentifierFromMethodToken(reader, metadataToken);
                            hasDelegateInstance = false;
                        }
                        else if (opCode.Value == OpCodes.Newobj.Value && !string.IsNullOrEmpty(lastLambdaIdentifier) && GetMethodName(reader, MetadataTokens.EntityHandle(metadataToken)).Equals(".ctor"))
                        {
                            hasDelegateInstance = true;
                        }
                        else if (!string.IsNullOrEmpty(lastLambdaIdentifier) && (IsLocalOrArgumentStore(opCode) || (IsFieldStore(opCode) && !lastInstructionWasDup)))
                        {
                            lastLambdaIdentifier = null;
                            lastString = null;
                            hasDelegateInstance = false;
                        }
                        else if ((opCode.Value == OpCodes.Call.Value || opCode.Value == OpCodes.Callvirt.Value) && !string.IsNullOrEmpty(lastLambdaIdentifier) && hasDelegateInstance && GetLambdaConsumerMethodName(reader, metadataToken) is string consumerMethodName)
                        {
                            if (consumerMethodName.Equals("Invoke"))
                            {
                                lastLambdaIdentifier = null;
                                lastString = null;
                                hasDelegateInstance = false;
                            }
                            else
                            {
                                routeNames.TryAdd(lastLambdaIdentifier, consumerMethodName.StartsWith("Map", StringComparison.Ordinal) && !string.IsNullOrEmpty(lastString) ? $"{consumerMethodName}_{lastString}" : consumerMethodName);
                                lastLambdaIdentifier = null;
                                lastString = null;
                                hasDelegateInstance = false;
                            }
                        }

                        lastInstructionWasDup = opCode.Value == OpCodes.Dup.Value;
                    }
                }
                catch
                {
                    // The call graph scan will report whatever
                    // metadata can be safely read.
                }
            }
        }

        return routeNames;
    }

    private static string? GetLambdaConsumerMethodName(MetadataReader reader, int metadataToken)
    {
        EntityHandle methodHandle = MetadataTokens.EntityHandle(metadataToken);

        string methodName = GetMethodName(reader, methodHandle);

        return methodName.Equals(".ctor") ? null : methodName;
    }

    private static bool IsLocalOrArgumentStore(OpCode opCode)
    {
        return opCode.Value == OpCodes.Starg.Value ||
               opCode.Value == OpCodes.Starg_S.Value ||
               opCode.Value == OpCodes.Stloc.Value ||
               opCode.Value == OpCodes.Stloc_S.Value ||
               opCode.Value == OpCodes.Stloc_0.Value ||
               opCode.Value == OpCodes.Stloc_1.Value ||
               opCode.Value == OpCodes.Stloc_2.Value ||
               opCode.Value == OpCodes.Stloc_3.Value;
    }

    private static bool IsFieldStore(OpCode opCode)
    {
        return opCode.Value == OpCodes.Stfld.Value || opCode.Value == OpCodes.Stsfld.Value;
    }

    private static string GetMethodName(MetadataReader reader, EntityHandle methodHandle)
    {
        return methodHandle.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)methodHandle).Name),
            HandleKind.MemberReference => reader.GetString(reader.GetMemberReference((MemberReferenceHandle)methodHandle).Name),
            HandleKind.MethodSpecification => GetMethodName(reader, reader.GetMethodSpecification((MethodSpecificationHandle)methodHandle).Method),
            _ => string.Empty
        };
    }

    private static string? GetLambdaIdentifierFromMethodToken(MetadataReader reader, int metadataToken)
    {
        EntityHandle methodHandle = MetadataTokens.EntityHandle(metadataToken);

        if (methodHandle.Kind != HandleKind.MethodDefinition)
            return null;

        string methodName = reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)methodHandle).Name);

        string lambdaIdentifier = GetLambdaIdentifier(methodName);

        return string.IsNullOrEmpty(lambdaIdentifier) ? null : lambdaIdentifier;
    }

    // IL INSTRUCTION READER
    private static bool TryReadInstruction(ref BlobReader reader, out OpCode opCode, out int metadataToken)
    {
        opCode = default;
        metadataToken = 0;

        if (reader.RemainingBytes == 0)
            return false;

        short opcodeValue = reader.ReadByte();

        // Handle two-byte IL opcodes (0xFE xx)
        if (opcodeValue == 0xFE)
        {
            if (reader.RemainingBytes == 0)
                return false;

            opcodeValue = (short)(0xFE00 | reader.ReadByte());
        }

        if (!IlOpCodes.TryGetValue(opcodeValue, out opCode))
            return false;

        // Handle switch
        if (opCode.OperandType == OperandType.InlineSwitch)
        {
            if (reader.RemainingBytes < 4)
                return false;

            int targetCount = reader.ReadInt32();

            if (targetCount < 0)
                return false;

            int targetBytes = checked(targetCount * 4);

            if (reader.RemainingBytes < targetBytes)
                return false;

            reader.Offset += targetBytes;
            metadataToken = -targetCount;                       // not a token: the number of switch cases (used for complexity)

            return true;
        }

        // Handle regular operand
        int operandSize = GetOperandSize(opCode.OperandType);

        if (reader.RemainingBytes < operandSize)
            return false;

        // If this instruction has a metadata token, preserve it.
        if (operandSize == 4 && IsMetadataTokenOperand(opCode.OperandType))
            metadataToken = reader.ReadInt32();
        else
            reader.Offset += operandSize;

        return true;
    }

    private static int GetOperandSize(OperandType operandType)
    {
        return operandType switch
        {
            OperandType.InlineBrTarget or
            OperandType.InlineField or
            OperandType.InlineI or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType or
            OperandType.ShortInlineR
                => 4,

            OperandType.InlineI8 or
            OperandType.InlineR
                => 8,

            OperandType.InlineVar
                => 2,

            OperandType.ShortInlineBrTarget or
            OperandType.ShortInlineI or
            OperandType.ShortInlineVar
                => 1,

            _ => 0
        };
    }

    private static bool IsMetadataTokenOperand(OperandType operandType)
    {
        return operandType is
            OperandType.InlineField or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType;
    }

    private static Dictionary<short, OpCode> CreateIlOpCodeMap()
    {
        var opCodes = new Dictionary<short, OpCode>();

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(OpCode))
            {
                OpCode opCode = (OpCode)field.GetValue(null)!;
                opCodes[opCode.Value] = opCode;
            }
        }

        return opCodes;
    }

    // LAMBDA DISPLAY HELPERS
    private static string GetLambdaDisplayName(string generatedTypeName, string generatedMethodName, Dictionary<string, string> lambdaBindingNames)
    {
        string generatedName = generatedMethodName.Equals("MoveNext") ? generatedTypeName : generatedMethodName;

        string lambdaIdentifier = GetLambdaIdentifier(generatedName);

        if (string.IsNullOrEmpty(lambdaIdentifier))
            return string.Empty;

        if (lambdaBindingNames.TryGetValue(lambdaIdentifier, out string? bindingName))
        {
            string containingMethodName = GetLambdaContainingMethodName(generatedName);
            return string.IsNullOrEmpty(containingMethodName) ? bindingName : $"{containingMethodName}.{bindingName}";
        }

        string fallbackContainingMethodName = GetLambdaContainingMethodName(generatedName);

        return string.IsNullOrEmpty(fallbackContainingMethodName) ? lambdaIdentifier : $"{fallbackContainingMethodName}.{lambdaIdentifier}";
    }

    private static string GetLambdaIdentifier(string generatedName)
    {
        int lambdaIndex = generatedName.IndexOf("b__", StringComparison.Ordinal);

        if (lambdaIndex < 0)
            return string.Empty;

        int identifierStart = lambdaIndex;

        int identifierEnd = generatedName.IndexOf('>', identifierStart);

        return identifierEnd >= identifierStart ? generatedName.Substring(identifierStart, identifierEnd - identifierStart) : generatedName.Substring(identifierStart);
    }

    private static string GetLambdaContainingMethodName(string generatedName)
    {
        int lambdaIndex = generatedName.IndexOf("b__", StringComparison.Ordinal);

        if (lambdaIndex < 0)
            return string.Empty;

        int methodStart = generatedName.LastIndexOf('<', lambdaIndex);

        int methodEnd = generatedName.IndexOf('>', methodStart + 1);

        if (methodStart < 0 || methodEnd <= methodStart)
            return string.Empty;

        return generatedName.Substring(methodStart + 1, methodEnd - methodStart - 1).TrimStart('<');
    }

    // SIGNATURE TYPE PROVIDER
    internal sealed class MethodSignatureTypeProvider : ISignatureTypeProvider<string, object>
    {
        private readonly MetadataReader _reader;

        public MethodSignatureTypeProvider(MetadataReader reader)
        {
            _reader = reader;
        }

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            if (shape.Rank == 1)
                return $"{elementType}[]";

            return $"{elementType}[{new string(',', shape.Rank - 1)}]";
        }

        public string GetByReferenceType(string elementType)
        {
            return $"{elementType}&";
        }

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            var parameters = string.Join(", ", signature.ParameterTypes);

            return $"delegate*<{parameters}>";
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            return $"{genericType}<{string.Join(", ", typeArguments)}>";
        }

        public string GetGenericMethodParameter(object genericContext, int index)
        {
            return $"!!{index}";
        }

        public string GetGenericTypeParameter(object genericContext, int index)
        {
            return $"!{index}";
        }

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            return unmodifiedType;
        }

        public string GetPinnedType(string elementType)
        {
            return elementType;
        }

        public string GetPointerType(string elementType)
        {
            return $"{elementType}*";
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return typeCode switch
            {
                PrimitiveTypeCode.Void => "void",
                PrimitiveTypeCode.Boolean => "bool",
                PrimitiveTypeCode.Char => "char",
                PrimitiveTypeCode.SByte => "sbyte",
                PrimitiveTypeCode.Byte => "byte",
                PrimitiveTypeCode.Int16 => "short",
                PrimitiveTypeCode.UInt16 => "ushort",
                PrimitiveTypeCode.Int32 => "int",
                PrimitiveTypeCode.UInt32 => "uint",
                PrimitiveTypeCode.Int64 => "long",
                PrimitiveTypeCode.UInt64 => "ulong",
                PrimitiveTypeCode.Single => "float",
                PrimitiveTypeCode.Double => "double",
                PrimitiveTypeCode.String => "string",
                PrimitiveTypeCode.IntPtr => "nint",
                PrimitiveTypeCode.UIntPtr => "nuint",
                PrimitiveTypeCode.Object => "object",
                _ => typeCode.ToString()
            };
        }

        public string GetSZArrayType(string elementType)
        {
            return $"{elementType}[]";
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            return GetTypeDefinitionFullName(reader, handle);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            return GetStringFromEntityHandle(reader, handle);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            TypeSpecification specification = reader.GetTypeSpecification(handle);

            return specification.DecodeSignature(this, genericContext);
        }
    }
}