using System.Reflection;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Unit
{
    /// <summary>
    /// CI runs the Unit and the Integration tests as two filtered runs (by namespace). A test class in neither namespace would be skipped by
    /// both without anyone noticing - which is exactly what happened to four tests before this guard existed.
    /// </summary>
    public class TestStructureTests
    {
        private static IEnumerable<Type> TestClasses() =>
            typeof(TestStructureTests).Assembly.GetTypes().Where(t => t.IsClass && t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttributes(typeof(FactAttribute), true).Length > 0));

        [Fact]
        public void Every_test_class_is_a_unit_or_an_integration_test_so_neither_CI_filter_skips_it()
        {
            var stray = TestClasses().Where(t => t.Namespace is not ("DotNetVisualizer.Web.Tests.Unit" or "DotNetVisualizer.Web.Tests.Integration")).Select(t => t.FullName).ToList();
            Assert.Empty(stray);
        }

        [Fact]
        public void Both_kinds_exist_and_the_integration_tests_use_the_sample_assemblies()
        {
            var classes = TestClasses().ToList();
            Assert.Contains(classes, t => t.Namespace!.EndsWith(".Unit"));
            Assert.Contains(classes, t => t.Namespace!.EndsWith(".Integration") && t.Name == "SampleAssemblyTests");
            Assert.True(File.Exists(SamplePaths.Inheritance), "the sample assemblies are built and copied next to the tests");
        }
    }
}
