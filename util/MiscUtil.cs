using System.Reflection.Metadata;

namespace DotNetVisualizer.Web.util
{
    public static class MiscUtil
    {
        public static string GetStringFromEntityHandle(MetadataReader reader, EntityHandle handle)
        {
            try
            {
                if (handle.IsNil) return string.Empty;

                if (handle.Kind == HandleKind.TypeDefinition)
                {
                    var td = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                    string ns = reader.GetString(td.Namespace);
                    string name = reader.GetString(td.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                else if (handle.Kind == HandleKind.TypeReference)
                {
                    var tr = reader.GetTypeReference((TypeReferenceHandle)handle);
                    string ns = reader.GetString(tr.Namespace);
                    string name = reader.GetString(tr.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                else if (handle.Kind == HandleKind.TypeSpecification)
                {
                    return "GenericTypeSpecification";
                }
            }
            catch { }
            return string.Empty;
        }
    }
}
