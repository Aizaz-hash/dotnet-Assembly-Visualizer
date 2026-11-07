using System.Reflection.Metadata;

namespace DotNetVisualizer.Core;

/// <summary>How the engine names types: the metadata helpers shared by the analyzer and the API reader.</summary>
public static class MetadataNames
{
    public static string GetStringFromEntityHandle(MetadataReader reader, EntityHandle handle)
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

    public static bool IsTopLevelStatementsType(MetadataReader reader, TypeDefinition typeDef)
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

    public static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinitionHandle typeHandle)
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

    public static string GetDisplayTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        string name = reader.GetString(typeDef.Name);

        return IsTopLevelStatementsType(reader, typeDef) && name.StartsWith("<") && name.EndsWith(">$") ? name.Substring(1, name.Length - 3) : name;
    }
}
