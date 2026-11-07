using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using static DotNetVisualizer.Core.IlReader;
using static DotNetVisualizer.Core.MetadataNames;

namespace DotNetVisualizer.Core;

public static class CallGraphReader
{
    /// <summary>
    /// Reads the types, the call graph, the risky-API calls and the method sizes of one assembly from its metadata and IL
    /// (nothing is loaded or run). Nothing the reader cannot read stops the rest, and nothing is dropped silently: every skipped
    /// item is counted in <see cref="CallGraph.Skipped"/>.
    /// </summary>
    /// <param name="hashBodies">Also hash every method body (see <see cref="Checks.Fingerprints.OfMethodBody"/>), for comparing two builds.</param>
    /// <param name="mark">Called after each stage with its name (the web app uses it for its timing report).</param>
    public static CallGraph Read(PEReader peReader, MetadataReader mdReader, Action<string>? mark = null, bool hashBodies = false)
    {
        int skippedTypes = 0, skippedMethods = 0, skippedInterfaces = 0, unreadableBodies = 0;
        var inheritanceNodes = new List<InheritanceNode>();
        var inheritanceEdges = new List<InheritanceEdge>();
        var classList = new List<string>();

        var methodNodes = new List<MethodNode>();
        // parent class -> ids of its method nodes in registration order (replaces a linear scan over every node)
        var methodIdsByClass = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var methodEdges = new List<MethodEdge>();
        var callPairs = new List<(string From, string To)>();
        // Constructors are only listed when something calls them or they call something: a compiler-generated default
        // constructor that just calls object() would otherwise add a meaningless node to every class.
        var pendingConstructors = new List<(string Id, MethodNode Node, string Name, string Class)>();
        var methodDisplay = new Dictionary<string, (string Name, string Class)>(StringComparer.OrdinalIgnoreCase);   // id -> label and owning class, for the metrics
        // Calls to APIs that deserve a security look (see RiskyApiRules): rule + api -> how often and from where.
        var riskHits = new Dictionary<(RiskyApiRule Rule, string Api), RiskHit>();
        var riskByToken = new Dictionary<int, (RiskyApiRule Rule, string Api)?>();
        // Code metrics: cyclomatic complexity and IL size per method, and what dead-code detection needs.
        var methodMetrics = new Dictionary<string, (int Complexity, int IlSize)>(StringComparer.OrdinalIgnoreCase);
        var methodMeta = new Dictionary<string, (MethodDefinitionHandle Handle, MethodAttributes Attributes, bool HasAttributes, string Name, string ParentClass)>(StringComparer.OrdinalIgnoreCase);
        var delegateTargets = new HashSet<int>();            // methods whose address is taken (ldftn / ldvirtftn): lambdas, method groups, events
        var bodyHashes = hashBodies ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;   // id -> hash of the body (only when asked: it costs a second pass over the IL)
        var staticInitBodies = new List<int>();              // static constructors are not graph nodes, but what they call / point to still counts as used
        var resolvedCallTargets = new Dictionary<int, (string Id, string ParentClass, bool IsInternal)>();

        var registeredMethodIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokenToUniqueMethodIdMap = new Dictionary<EntityHandle, string>();

        // Keep track of methods that actually have executable bodies to scan in pass 2
        var methodsToScan = new List<(string CallerMethodId, string CallerClass, int Rva)>();

        var lambdaBindingNames = GetLambdaBindingNames(peReader, mdReader);
        mark?.Invoke("lambda name scan (IL)");


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

                    inheritanceNodes.Add(new InheritanceNode(
                        fullName,
                        GetDisplayTypeName(mdReader, typeDef),
                        isInterface ? "Interface" : "Class",
                        // the real namespace (of the outermost type): a nested type's full name also contains dots, so the browser
                        // cannot recover the namespace from the name without inventing one per enclosing type
                        GetOutermostNamespace(mdReader, typeDef),
                        !typeDef.GetDeclaringType().IsNil,
                        MetadataTokens.GetToken(typeHandle)));          // lets the C# view ask for this type's source
                }

                // Base inheritance
                if (!typeDef.BaseType.IsNil)
                {
                    string baseName = GetStringFromEntityHandle(mdReader, typeDef.BaseType);

                    if (!string.IsNullOrEmpty(baseName) && !baseName.Equals("System.Object"))
                    {
                        inheritanceEdges.Add(new InheritanceEdge(fullName, baseName, "Inherits"));
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
                            inheritanceEdges.Add(new InheritanceEdge(fullName, ifaceName, "Implements"));
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
                            pendingConstructors.Add((methodUniqueId, new MethodNode(methodUniqueId, ctorLabel, fullName, MetadataTokens.GetToken(methodHandle)), ctorLabel, fullName));
                        }
                        else if (isNewMethodNode)
                        {
                            if (!methodIdsByClass.TryGetValue(fullName, out var idsOfClass))
                                methodIdsByClass[fullName] = idsOfClass = new List<string>();
                            idsOfClass.Add(methodUniqueId);

                            methodDisplay[methodUniqueId] = (methodSignature, fullName);
                            methodNodes.Add(new MethodNode(methodUniqueId, methodSignature, fullName, MetadataTokens.GetToken(methodHandle)));
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

        mark?.Invoke("type + method discovery");

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
                if (bodyHashes != null) bodyHashes[item.CallerMethodId] = Checks.Fingerprints.OfMethodBody(body, mdReader);

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
                            methodEdges.Add(new MethodEdge(item.CallerMethodId, calleeMethodId, "Calls", item.CallerClass, calleeParentClass));
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
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in callPairs) { connected.Add(from); connected.Add(to); }
        foreach (var (id, node, ctorName, ctorClass) in pendingConstructors)
            if (connected.Contains(id)) { methodNodes.Add(node); methodDisplay[id] = (ctorName, ctorClass); }

        mark?.Invoke("call analysis (IL scan)");

        return new CallGraph
        {
            ClassList = classList,
            InheritanceNodes = inheritanceNodes,
            InheritanceEdges = inheritanceEdges,
            MethodNodes = methodNodes,
            MethodEdges = methodEdges,
            CallPairs = callPairs,
            MethodDisplay = methodDisplay,
            RiskHits = riskHits,
            MethodMetrics = methodMetrics,
            MethodMeta = methodMeta,
            DelegateTargets = delegateTargets,
            StaticInitBodies = staticInitBodies,
            MethodBodyHashes = bodyHashes ?? new Dictionary<string, string>(),
            Skipped = new SkippedItems(skippedTypes, skippedMethods, skippedInterfaces, unreadableBodies)
        };
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
            // a member of a generic instantiation (Expression<Func<int>>.Compile) has a type specification as its parent; it still names the
            // generic type, so it must not be skipped
            if (member.Parent.Kind != HandleKind.TypeReference && member.Parent.Kind != HandleKind.TypeSpecification) return null;

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

    private static bool IsCompilerGeneratedName(string name)
    {
        return name.StartsWith("<");
    }

    private static bool IsLambdaGeneratedType(string name)
    {
        return name.StartsWith("<>c") || name.Contains("b__");
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


    private static string GetOutermostNamespace(MetadataReader reader, TypeDefinition typeDef)
    {
        while (!typeDef.GetDeclaringType().IsNil)
            typeDef = reader.GetTypeDefinition(typeDef.GetDeclaringType());
        return reader.GetString(typeDef.Namespace);
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
}
