using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using DotNetVisualizer.Core.ApiDiff;
using static DotNetVisualizer.Core.IlReader;
using static DotNetVisualizer.Core.MetadataNames;

namespace DotNetVisualizer.Core.Checks;

/// <summary>SHA-256 digests (like an image digest): the same value means the same content.</summary>
public static class Fingerprints
{
    public static string OfFile(string path)
    {
        using var stream = AssemblyFile.Open(path);
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(stream));
    }

    /// <summary>
    /// Hash of the public API: every type and every member a caller outside the assembly can reach, as sorted lines. The same value
    /// means the same public API, whatever happened inside the method bodies.
    /// </summary>
    public static string OfApi(ApiSurface api)
    {
        var text = new StringBuilder();
        foreach (var type in api.Types.Values.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            text.Append("T|").Append(type.FullName).Append('|').Append(type.Kind).Append('|').Append(type.Access).Append('|')
                .Append(type.IsAbstract).Append(type.IsSealed).Append(type.IsObsolete).Append('|').Append(type.BaseType).Append('|')
                .Append(string.Join(",", type.Interfaces.OrderBy(i => i, StringComparer.Ordinal))).Append('\n');
            foreach (var m in type.Members.Values.OrderBy(m => m.Key, StringComparer.Ordinal))
                text.Append("M|").Append(type.FullName).Append('|').Append(m.Kind).Append('|').Append(m.Key).Append('|').Append(m.Returns).Append('|').Append(m.Access).Append('|')
                    .Append(m.IsStatic).Append(m.IsAbstract).Append(m.IsVirtual).Append(m.IsSealed).Append(m.IsObsolete).Append(m.HasGetter).Append(m.HasSetter).Append('|')
                    .Append(m.Constant).Append('\n');
        }
        return OfText(text.ToString());
    }

    /// <summary>
    /// Hash of one method body that does not change when only other members were added or removed: opcodes and plain operands are
    /// hashed as they are, and an operand that is a metadata token is replaced by the name it points to (tokens are numbered by position).
    /// </summary>
    public static string OfMethodBody(MethodBodyBlock body, MetadataReader md)
    {
        var il = body.GetILBytes() ?? Array.Empty<byte>();
        var reader = body.GetILReader();
        using var sha = SHA256.Create();
        var stream = new MemoryStream();
        while (true)
        {
            int start = reader.Offset;
            if (!TryReadInstruction(ref reader, out var opCode, out int token)) break;
            int end = reader.Offset;
            int opcodeLength = opCode.Size;
            stream.Write(il, start, opcodeLength);
            if (token > 0 && opCode.OperandType != System.Reflection.Emit.OperandType.InlineSwitch)
            {
                var name = Encoding.UTF8.GetBytes(ResolveToken(md, token));
                stream.Write(name, 0, name.Length);
                stream.WriteByte(0);
            }
            else if (end - start - opcodeLength > 0)
                stream.Write(il, start + opcodeLength, end - start - opcodeLength);
        }
        stream.Position = 0;
        return Hex(sha.ComputeHash(stream));
    }

    private static string ResolveToken(MetadataReader md, int token)
    {
        try
        {
            if ((token >> 24) == 0x70)
                return "str:" + md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
            var handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var m = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return "m:" + GetTypeDefinitionFullName(md, m.GetDeclaringType()) + "." + md.GetString(m.Name) + "/" + m.GetParameters().Count;
                }
                case HandleKind.FieldDefinition:
                {
                    var f = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return "f:" + GetTypeDefinitionFullName(md, f.GetDeclaringType()) + "." + md.GetString(f.Name);
                }
                case HandleKind.MemberReference:
                {
                    var r = md.GetMemberReference((MemberReferenceHandle)handle);
                    return "r:" + GetStringFromEntityHandle(md, r.Parent) + "." + md.GetString(r.Name) + "/" + md.GetBlobReader(r.Signature).Length;
                }
                case HandleKind.MethodSpecification:
                {
                    var s = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return "s:" + ResolveToken(md, MetadataTokens.GetToken(s.Method));
                }
                case HandleKind.TypeDefinition:
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    return "t:" + GetStringFromEntityHandle(md, (EntityHandle)handle);
                default:
                    return "x:" + handle.Kind;
            }
        }
        catch
        {
            return "?";
        }
    }

    private static string OfText(string text)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
