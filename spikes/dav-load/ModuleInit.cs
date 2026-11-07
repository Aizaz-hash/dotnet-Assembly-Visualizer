using System;
using System.IO;
using System.Management.Automation;
using System.Reflection;

namespace DavLoadSpike
{
    /// <summary>
    /// Windows PowerShell 5.1 has no binding redirects, so a library that was built against another version of a shared
    /// package (here System.Runtime.CompilerServices.Unsafe) fails to load. When the normal lookup fails, use the copy
    /// that ships next to the module, whatever its version. It runs only for assemblies that could not be found.
    /// </summary>
    public sealed class ModuleInit : IModuleAssemblyInitializer
    {
        public void OnImport() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        private static Assembly? Resolve(object? sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            var dir = Path.GetDirectoryName(typeof(ModuleInit).Assembly.Location);
            if (name == null || dir == null) return null;
            var file = Path.Combine(dir, name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        }
    }
}
