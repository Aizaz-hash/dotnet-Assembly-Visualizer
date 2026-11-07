using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetVisualizer.Core.Checks;

/// <summary>Opens an assembly file for the checks, with the same refusals and messages as the web app.</summary>
public static class AssemblyFile
{
    /// <summary>Reads the call graph of the assembly at <paramref name="path"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a .NET assembly.</exception>
    public static CallGraph ReadGraph(string path, bool hashBodies = false)
    {
        using var stream = Open(path);
        using var pe = new PEReader(stream);
        RequireMetadata(pe, path);
        return CallGraphReader.Read(pe, pe.GetMetadataReader(), null, hashBodies);
    }

    /// <summary>Opens for reading while another program (a build) may still hold the file open.</summary>
    public static FileStream Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}", path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public static void RequireMetadata(PEReader pe, string path)
    {
        if (!pe.HasMetadata)
            throw new InvalidDataException($"{path} is not a .NET assembly: it has no .NET metadata (a native / unmanaged library or another kind of file).");
    }
}
