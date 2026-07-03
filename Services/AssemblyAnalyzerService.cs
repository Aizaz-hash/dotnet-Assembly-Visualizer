using DotNetVisualizer.Web.Interfaces;
using DotNetVisualizer.Web.util;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetVisualizer.Web.Services
{

    public class AssemblyAnalyzerService : IAssemblyAnalyzer
    {
        private readonly ICircularDependencyDetector _detector;
        public AssemblyAnalyzerService(ICircularDependencyDetector detector)
        {
            _detector = detector;
        }
        public object Analyze(IFormFile file)
        {
            var inheritanceNodes = new List<object>();
            var inheritanceEdges = new List<object>();
            var classList = new List<object>(); // Changed to object list to hold metadata

            var methodNodes = new List<object>();
            var methodEdges = new List<object>();

            var registeredMethodIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokenToUniqueMethodIdMap = new Dictionary<EntityHandle, string>();
            var methodsToScan = new List<(string CallerMethodId, int Rva)>();

            using var stream = file.OpenReadStream();
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                throw new InvalidDataException("The uploaded file does not contain valid .NET assembly metadata.");
            }

            MetadataReader mdReader = peReader.GetMetadataReader();

            // Extract the real Assembly name from metadata
            string assemblyName = file.FileName;
            if (mdReader.IsAssembly)
            {
                var asmDef = mdReader.GetAssemblyDefinition();
                assemblyName = mdReader.GetString(asmDef.Name);
            }

            // --- FIRST PASS: Map out Structures and cache targets ---
            foreach (TypeDefinitionHandle typeHandle in mdReader.TypeDefinitions)
            {
                try
                {
                    TypeDefinition typeDef = mdReader.GetTypeDefinition(typeHandle);

                    string name = mdReader.GetString(typeDef.Name);
                    if (name.StartsWith("<") || name.Equals("<Module>")) continue;

                    string ns = mdReader.GetString(typeDef.Namespace);
                    string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

                    bool isInterface = (typeDef.Attributes & TypeAttributes.Interface) != 0;

                    // Track class along with its source assembly definitions
                    classList.Add(new { fullName, assembly = assemblyName });

                    inheritanceNodes.Add(new { id = fullName, name = name, type = isInterface ? "Interface" : "Class" });

                    if (!typeDef.BaseType.IsNil)
                    {
                        string baseName = MiscUtil.GetStringFromEntityHandle(mdReader, typeDef.BaseType);
                        if (!string.IsNullOrEmpty(baseName) && !baseName.Equals("System.Object"))
                        {
                            inheritanceEdges.Add(new { from = fullName, to = baseName, relation = "Inherits" });
                        }
                    }

                    foreach (InterfaceImplementationHandle ifaceHandle in typeDef.GetInterfaceImplementations())
                    {
                        try
                        {
                            InterfaceImplementation ifaceImpl = mdReader.GetInterfaceImplementation(ifaceHandle);
                            string ifaceName = MiscUtil.GetStringFromEntityHandle(mdReader, ifaceImpl.Interface);
                            if (!string.IsNullOrEmpty(ifaceName))
                            {
                                inheritanceEdges.Add(new { from = fullName, to = ifaceName, relation = "Implements" });
                            }
                        }
                        catch { }
                    }

                    if (isInterface) continue;

                    foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
                    {
                        try
                        {
                            MethodDefinition methodDef = mdReader.GetMethodDefinition(methodHandle);
                            string methodName = mdReader.GetString(methodDef.Name);

                            if (methodName.StartsWith("<") || methodName.Equals(".ctor") || methodName.Equals(".cctor")) continue;

                            // Bug 1 Fix: Read formal parameter names from the method definition metadata
                            var parameterNamesList = new List<string>();
                            foreach (var paramHandle in methodDef.GetParameters())
                            {
                                var param = mdReader.GetParameter(paramHandle);
                                if (param.SequenceNumber == 0) continue; // Skip sequence 0 (return type relative attribute token)

                                string paramName = mdReader.GetString(param.Name);
                                if (!string.IsNullOrEmpty(paramName))
                                {
                                    parameterNamesList.Add(paramName);
                                }
                            }

                            string parameterSignature = string.Join(", ", parameterNamesList);
                            string baseMethodId = $"{fullName}.{methodName}({parameterSignature})";
                            string methodUniqueId = baseMethodId;
                            int duplicateIndex = 1;

                            while (registeredMethodIds.Contains(methodUniqueId))
                            {
                                methodUniqueId = $"{fullName}.{methodName}_{duplicateIndex}({parameterSignature})";
                                duplicateIndex++;
                            }

                            registeredMethodIds.Add(methodUniqueId);
                            tokenToUniqueMethodIdMap[methodHandle] = methodUniqueId;

                            methodNodes.Add(new { id = methodUniqueId, name = methodName, parentClass = fullName });
                            methodEdges.Add(new { from = methodUniqueId, to = fullName, relation = "DeclaredIn" });

                            if (methodDef.RelativeVirtualAddress > 0)
                            {
                                methodsToScan.Add((methodUniqueId, methodDef.RelativeVirtualAddress));
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // --- SECOND PASS: Fast-skipping IL Pointer Reader ---
            foreach (var item in methodsToScan)
            {
                try
                {
                    MethodBodyBlock body = peReader.GetMethodBody(item.Rva);
                    BlobReader ilReader = body.GetILReader();

                    while (ilReader.RemainingBytes > 0)
                    {
                        byte opCode = ilReader.ReadByte();
                        if (opCode == 0x28 || opCode == 0x6F)
                        {
                            if (ilReader.RemainingBytes >= 4)
                            {
                                int tokenVal = ilReader.ReadInt32();
                                EntityHandle calledEntityHandle = MetadataTokens.EntityHandle(tokenVal);
                                string calleeMethodId = string.Empty;
                                string calleeParentClass = string.Empty;

                                if (calledEntityHandle.Kind == HandleKind.MethodDefinition)
                                {
                                    tokenToUniqueMethodIdMap.TryGetValue(calledEntityHandle, out calleeMethodId!);
                                }
                                else if (calledEntityHandle.Kind == HandleKind.MemberReference)
                                {
                                    var memRef = mdReader.GetMemberReference((MemberReferenceHandle)calledEntityHandle);
                                    if (memRef.Parent.Kind == HandleKind.TypeReference || memRef.Parent.Kind == HandleKind.TypeDefinition)
                                    {
                                        calleeParentClass = MiscUtil.GetStringFromEntityHandle(mdReader, memRef.Parent);
                                        string targetMethodName = mdReader.GetString(memRef.Name);

                                        if (!targetMethodName.StartsWith("<") && !targetMethodName.Equals(".ctor"))
                                        {
                                            calleeMethodId = $"{calleeParentClass}.{targetMethodName}()";
                                        }
                                    }
                                }

                                if (!string.IsNullOrEmpty(calleeMethodId) && !calleeMethodId.Equals(item.CallerMethodId))
                                {
                                    string callerParent = item.CallerMethodId.Split('(')[0];
                                    int lastDot = callerParent.LastIndexOf('.');
                                    callerParent = lastDot > -1 ? callerParent.Substring(0, lastDot) : callerParent;

                                    methodEdges.Add(new
                                    {
                                        from = item.CallerMethodId,
                                        to = calleeMethodId,
                                        relation = "Calls",
                                        fromClass = callerParent,
                                        toClass = calleeParentClass
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            var methodAdjacencyList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic edge in methodEdges)
            {
                if (edge.relation == "Calls")
                {
                    string fromNode = edge.from;
                    string toNode = edge.to;
                    if (!methodAdjacencyList.ContainsKey(fromNode))
                        methodAdjacencyList[fromNode] = new List<string>();
                    methodAdjacencyList[fromNode].Add(toNode);
                }
            }

            List<List<string>> detectedDeadlocks = _detector.DetectCycles(methodAdjacencyList);

            return new
            {
                assemblyName = assemblyName,
                classes = classList,
                inheritance = new { nodes = inheritanceNodes, edges = inheritanceEdges },
                methodGraph = new { nodes = methodNodes, edges = methodEdges },
                deadlocks = detectedDeadlocks
            };
        }
    }
}
