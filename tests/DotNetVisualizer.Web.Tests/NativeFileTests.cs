using DotNetVisualizer.Web.Services;
using Xunit;

namespace DotNetVisualizer.Web.Tests
{
    /// <summary>What a user sees when the file they opened is not .NET code.</summary>
    public class NativeFileTests
    {
        private static InvalidDataException Reject(string path)
        {
            using var stream = File.OpenRead(path);
            return Assert.Throws<InvalidDataException>(() => new AssemblyAnalyzerService(new CircularDependencyDetectorService()).Analyze(stream));
        }

        [Fact]
        public void The_exe_next_to_a_dotnet_application_is_explained_as_a_launcher_and_points_to_the_dll()
        {
            var launcher = Path.Combine(AppContext.BaseDirectory, "dotnet-Assembly-Visualizer.exe");
            if (!OperatingSystem.IsWindows() || !File.Exists(launcher)) return;             // the apphost exists on Windows builds only
            var ex = Reject(launcher);
            Assert.Contains("native launcher", ex.Message);
            Assert.Contains(".dll with the same name", ex.Message);
        }

        [Fact]
        public void A_native_library_is_rejected_with_a_message_that_says_what_it_is()
        {
            var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            if (!OperatingSystem.IsWindows() || !File.Exists(kernel32)) return;             // a native PE to try exists on Windows only
            var ex = Reject(kernel32);
            Assert.Contains("not a .NET assembly", ex.Message);
            Assert.Contains("native", ex.Message);
        }

        [Fact]
        public void A_managed_exe_is_analysed_like_a_dll()
        {
            var msbuild = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "MSBuild.exe");
            if (!OperatingSystem.IsWindows() || !File.Exists(msbuild)) return;              // a .NET Framework .exe to try exists on Windows only
            using var stream = File.OpenRead(msbuild);
            var result = new AssemblyAnalyzerService(new CircularDependencyDetectorService()).Analyze(stream);
            Assert.NotNull(result);
        }
    }
}
