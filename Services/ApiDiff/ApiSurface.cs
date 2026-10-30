using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotNetVisualizer.Web.Services;

namespace DotNetVisualizer.Web.Services.ApiDiff;

/// <summary>One member a caller outside the assembly can reach.</summary>
public sealed record ApiMember(
    string Kind,              // method | constructor | property | field | event
    string Key,               // identity used to match old and new: name + parameter types
    string Display,           // what a person reads: Name(type a, type b)
    string Returns,           // return type / property type / field type
    string Access,            // public | protected | protected internal
    bool IsStatic, bool IsAbstract, bool IsVirtual, bool IsSealed, bool IsObsolete,
    string[] ParameterNames,
    string? Constant,         // value of a const field / enum member
    bool HasGetter, bool HasSetter);

public sealed record ApiType(
    string FullName, string Kind /* class | static class | struct | interface | enum | delegate */, string Access,
    bool IsAbstract, bool IsSealed, bool IsObsolete,
    string? BaseType, string[] Interfaces, Dictionary<string, ApiMember> Members);

public sealed record ApiSurface(string AssemblyName, string Version, Dictionary<string, ApiType> Types);

/// <summary>Reads the public API of an assembly from its metadata (nothing is loaded or run).</summary>
public static class ApiSurfaceReader
{
    /// <summary>Reads like C#: System.Func`2&lt;!!0, !!1&gt; becomes System.Func&lt;T0, T1&gt;. Only for display; keys keep the raw form.</summary>
    public static string Pretty(string text) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text, @"`\d+(?=<)", ""), @"!!?(\d+)", "T$1");

    public static ApiSurface Read(Stream assembly)
    {
        using var pe = new PEReader(assembly);
        if (!pe.HasMetadata) throw new InvalidDataException("The file does not contain valid .NET assembly metadata.");
        var md = pe.GetMetadataReader();
        var provider = new AssemblyAnalyzerService.MethodSignatureTypeProvider(md);

        string NameOf(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => provider.GetTypeFromReference(md, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(md, null!, (TypeSpecificationHandle)h, 0),
            _ => ""
        };

        bool IsObsolete(CustomAttributeHandleCollection attributes)
        {
            foreach (var handle in attributes)
            {
                var attr = md.GetCustomAttribute(handle);
                if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
                var parent = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
                if (parent.Kind == HandleKind.TypeReference && md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) == "ObsoleteAttribute") return true;
            }
            return false;
        }

        // a type is part of the API when it is public, or nested inside an exposed type with public/protected access
        var exposed = new Dictionary<TypeDefinitionHandle, bool>();
        bool IsExposed(TypeDefinitionHandle h)
        {
            if (exposed.TryGetValue(h, out var known)) return known;
            var t = md.GetTypeDefinition(h);
            var vis = t.Attributes & TypeAttributes.VisibilityMask;
            bool result = vis switch
            {
                TypeAttributes.Public => true,
                TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => IsExposed(t.GetDeclaringType()),
                _ => false
            };
            return exposed[h] = result;
        }

        var types = new Dictionary<string, ApiType>(StringComparer.Ordinal);
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            string name = md.GetString(t.Name);
            if (name == "<Module>" || name.StartsWith("<") || !IsExposed(th)) continue;

            string full = provider.GetTypeFromDefinition(md, th, 0);
            string? baseName = t.BaseType.IsNil ? null : NameOf(t.BaseType);
            bool isInterface = (t.Attributes & TypeAttributes.Interface) != 0;
            bool isAbstract = (t.Attributes & TypeAttributes.Abstract) != 0, isSealed = (t.Attributes & TypeAttributes.Sealed) != 0;
            string kind = isInterface ? "interface"
                : baseName == "System.Enum" ? "enum"
                : baseName == "System.ValueType" ? "struct"
                : baseName is "System.MulticastDelegate" or "System.Delegate" ? "delegate"
                : isAbstract && isSealed ? "static class" : "class";
            var nestedVis = t.Attributes & TypeAttributes.VisibilityMask;
            string access = nestedVis is TypeAttributes.NestedFamily ? "protected" : nestedVis is TypeAttributes.NestedFamORAssem ? "protected internal" : "public";

            var members = new Dictionary<string, ApiMember>(StringComparer.Ordinal);
            // methods that are really property / event accessors are shown through the property or event
            var accessors = new HashSet<MethodDefinitionHandle>();

            foreach (var ph in t.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var acc = p.GetAccessors();
                if (!acc.Getter.IsNil) accessors.Add(acc.Getter);
                if (!acc.Setter.IsNil) accessors.Add(acc.Setter);
                var best = !acc.Getter.IsNil ? acc.Getter : acc.Setter;
                if (best.IsNil) continue;
                var m = md.GetMethodDefinition(best);
                string? acs = MemberAccess(m.Attributes, isSealed);
                if (acs == null && !acc.Setter.IsNil) { m = md.GetMethodDefinition(acc.Setter); acs = MemberAccess(m.Attributes, isSealed); }
                if (acs == null) continue;
                try
                {
                    var sig = p.DecodeSignature(provider, null!);
                    string pname = md.GetString(p.Name);
                    string idx = sig.ParameterTypes.Length > 0 ? "[" + string.Join(", ", sig.ParameterTypes) + "]" : "";
                    bool hasGet = !acc.Getter.IsNil && MemberAccess(md.GetMethodDefinition(acc.Getter).Attributes, isSealed) != null;
                    bool hasSet = !acc.Setter.IsNil && MemberAccess(md.GetMethodDefinition(acc.Setter).Attributes, isSealed) != null;
                    members["P:" + pname + idx] = new ApiMember("property", "P:" + pname + idx, pname + idx, Pretty(sig.ReturnType), acs,
                        (m.Attributes & MethodAttributes.Static) != 0, (m.Attributes & MethodAttributes.Abstract) != 0,
                        (m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.Final) == 0, (m.Attributes & MethodAttributes.Final) != 0,
                        IsObsolete(p.GetCustomAttributes()), Array.Empty<string>(), null, hasGet, hasSet);
                }
                catch (BadImageFormatException) { /* skip a damaged property */ }
            }

            foreach (var eh in t.GetEvents())
            {
                var e = md.GetEventDefinition(eh);
                var acc = e.GetAccessors();
                if (!acc.Adder.IsNil) accessors.Add(acc.Adder);
                if (!acc.Remover.IsNil) accessors.Add(acc.Remover);
                if (acc.Adder.IsNil) continue;
                var m = md.GetMethodDefinition(acc.Adder);
                var acs = MemberAccess(m.Attributes, isSealed);
                if (acs == null) continue;
                string ename = md.GetString(e.Name);
                members["E:" + ename] = new ApiMember("event", "E:" + ename, ename, Pretty(NameOf(e.Type)), acs, (m.Attributes & MethodAttributes.Static) != 0,
                    (m.Attributes & MethodAttributes.Abstract) != 0, (m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.Final) == 0,
                    (m.Attributes & MethodAttributes.Final) != 0, IsObsolete(e.GetCustomAttributes()), Array.Empty<string>(), null, false, false);
            }

            foreach (var mh in t.GetMethods())
            {
                if (accessors.Contains(mh)) continue;
                var m = md.GetMethodDefinition(mh);
                string? acs = MemberAccess(m.Attributes, isSealed);
                if (acs == null) continue;
                string mname = md.GetString(m.Name);
                if (mname == ".cctor") continue;
                try
                {
                    var sig = m.DecodeSignature(provider, null!);
                    var pnames = m.GetParameters().Select(h => md.GetParameter(h)).Where(x => x.SequenceNumber > 0).OrderBy(x => x.SequenceNumber).Select(x => md.GetString(x.Name)).ToArray();
                    string shown = mname == ".ctor" ? "new" : mname;
                    string arity = sig.GenericParameterCount > 0 ? "`" + sig.GenericParameterCount : "";
                    string paramText = string.Join(", ", sig.ParameterTypes.Select((ty, i) => i < pnames.Length ? Pretty(ty) + " " + pnames[i] : Pretty(ty)));
                    string genericMark = sig.GenericParameterCount > 0 ? "<" + new string(',', sig.GenericParameterCount - 1) + ">" : "";
                    string key = (mname == ".ctor" ? "C:" : "M:") + mname + arity + "(" + string.Join(",", sig.ParameterTypes) + ")";
                    members[key] = new ApiMember(mname == ".ctor" ? "constructor" : "method", key, shown + genericMark + "(" + paramText + ")", Pretty(sig.ReturnType), acs,
                        (m.Attributes & MethodAttributes.Static) != 0, (m.Attributes & MethodAttributes.Abstract) != 0,
                        (m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.Final) == 0, (m.Attributes & MethodAttributes.Final) != 0 && (m.Attributes & MethodAttributes.Virtual) != 0,
                        IsObsolete(m.GetCustomAttributes()), pnames, null, false, false);
                }
                catch (BadImageFormatException) { /* skip a damaged signature */ }
            }

            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                string? acs = MemberAccess((MethodAttributes)((int)f.Attributes & 0x7), isSealed);        // field access bits equal the method ones
                if (acs == null) continue;
                string fname = md.GetString(f.Name);
                if (fname == "value__") continue;                                                           // the enum backing field
                string? constant = null;
                if (!f.GetDefaultValue().IsNil)
                {
                    try { constant = ConstantText(md, md.GetConstant(f.GetDefaultValue())); } catch { constant = null; }
                }
                string ftype = "";
                try { ftype = Pretty(f.DecodeSignature(provider, null!)); } catch (BadImageFormatException) { }
                members["F:" + fname] = new ApiMember("field", "F:" + fname, fname, ftype, acs, (f.Attributes & FieldAttributes.Static) != 0, false, false, false,
                    IsObsolete(f.GetCustomAttributes()), Array.Empty<string>(), constant, false, false);
            }

            var interfaces = t.GetInterfaceImplementations().Select(i => NameOf(md.GetInterfaceImplementation(i).Interface)).Where(n => n.Length > 0).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            types[full] = new ApiType(full, kind, access, isAbstract, isSealed, IsObsolete(t.GetCustomAttributes()), baseName, interfaces, members);
        }

        var asm = md.IsAssembly ? md.GetAssemblyDefinition() : default;
        return new ApiSurface(md.IsAssembly ? md.GetString(asm.Name) : "", md.IsAssembly ? asm.Version.ToString() : "", types);
    }

    /// <summary>"public" / "protected" / "protected internal", or null when callers outside the assembly cannot reach it.</summary>
    private static string? MemberAccess(MethodAttributes attributes, bool declaringTypeSealed)
    {
        switch (attributes & MethodAttributes.MemberAccessMask)
        {
            case MethodAttributes.Public: return "public";
            case MethodAttributes.Family: return declaringTypeSealed ? null : "protected";                  // only derived classes can reach it
            case MethodAttributes.FamORAssem: return declaringTypeSealed ? null : "protected internal";
            default: return null;
        }
    }

    private static string ConstantText(MetadataReader md, Constant c)
    {
        var blob = md.GetBlobReader(c.Value);
        return c.TypeCode switch
        {
            ConstantTypeCode.Boolean => blob.ReadBoolean().ToString(),
            ConstantTypeCode.Char => ((int)blob.ReadChar()).ToString(),
            ConstantTypeCode.SByte => blob.ReadSByte().ToString(),
            ConstantTypeCode.Byte => blob.ReadByte().ToString(),
            ConstantTypeCode.Int16 => blob.ReadInt16().ToString(),
            ConstantTypeCode.UInt16 => blob.ReadUInt16().ToString(),
            ConstantTypeCode.Int32 => blob.ReadInt32().ToString(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32().ToString(),
            ConstantTypeCode.Int64 => blob.ReadInt64().ToString(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64().ToString(),
            ConstantTypeCode.Single => blob.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Double => blob.ReadDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.String => "\"" + blob.ReadUTF16(blob.RemainingBytes) + "\"",
            ConstantTypeCode.NullReference => "null",
            _ => ""
        };
    }
}
