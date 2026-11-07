using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace DotNetVisualizer.Core;

/// <summary>Reads a signature as C#-like text (int, string[], List&lt;!0&gt;); generic parameters stay as !0 / !!0.</summary>
public sealed class MethodSignatureTypeProvider : ISignatureTypeProvider<string, object>
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
        return MetadataNames.GetTypeDefinitionFullName(reader, handle);
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        return MetadataNames.GetStringFromEntityHandle(reader, handle);
    }

    public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        TypeSpecification specification = reader.GetTypeSpecification(handle);

        return specification.DecodeSignature(this, genericContext);
    }
}
