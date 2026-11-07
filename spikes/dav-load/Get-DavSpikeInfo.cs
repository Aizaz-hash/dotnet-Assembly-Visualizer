using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DavLoadSpike
{
    [Cmdlet(VerbsCommon.Get, "DavSpikeInfo")]
    public sealed class GetDavSpikeInfoCommand : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string Path { get; set; } = "";

        protected override void ProcessRecord()
        {
            var full = GetUnresolvedProviderPathFromPSPath(Path);
            using var stream = File.OpenRead(full);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            var types = md.TypeDefinitions.Count;
            var methods = md.MethodDefinitions.Count;
            var name = md.GetString(md.GetAssemblyDefinition().Name);
            var srm = typeof(MetadataReader).Assembly;
            WriteObject(new
            {
                Assembly = name,
                Types = types,
                Methods = methods,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                MetadataLibrary = srm.GetName().Version?.ToString(),
                MetadataLibraryPath = srm.Location,
            });
        }
    }
}
