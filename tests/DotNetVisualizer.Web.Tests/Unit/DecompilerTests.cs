using DotNetVisualizer.Web.Services.Decompilation;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Unit
{
    public class DecompilerServiceTests
    {
        private static readonly byte[] Bytes = File.ReadAllBytes(typeof(DecompilerServiceTests).Assembly.Location);

        // appending bytes after the last section (the PE "overlay") gives a different hash and a file that still loads
        private static byte[] Variant(int n) => Bytes.Concat(Enumerable.Repeat((byte)n, n)).ToArray();

        private static int TypeToken => typeof(MetricsSubject).MetadataToken;
        private static int MethodToken => typeof(MetricsSubject).GetMethod(nameof(MetricsSubject.Simple))!.MetadataToken;

        [Fact]
        public void The_same_bytes_always_give_the_same_id()
        {
            var service = new DecompilerService();
            Assert.Equal(service.Open(Bytes), service.Open(Bytes));
            Assert.NotEqual(service.Open(Bytes), service.Open(Variant(1)));
        }

        [Fact]
        public async Task A_type_is_decompiled_to_csharp()
        {
            var service = new DecompilerService();
            var result = await service.DecompileAsync(service.Open(Bytes), TypeToken, default);

            Assert.NotNull(result);
            Assert.Equal("type", result!.Kind);
            Assert.Contains("class MetricsSubject", result.Code);
            Assert.Contains("Branchy", result.Code);
            Assert.False(result.Truncated);
        }

        [Fact]
        public async Task A_single_method_is_decompiled_without_the_rest_of_its_type()
        {
            var service = new DecompilerService();
            var result = await service.DecompileAsync(service.Open(Bytes), MethodToken, default);

            Assert.NotNull(result);
            Assert.Equal("method", result!.Kind);
            Assert.Contains("int Simple()", result.Code);
            Assert.Contains("return 1;", result.Code);
            Assert.DoesNotContain("Branchy", result.Code);
        }

        [Fact]
        public async Task Control_flow_is_recovered_not_dumped_as_il()
        {
            var service = new DecompilerService();
            int token = typeof(MetricsSubject).GetMethod(nameof(MetricsSubject.Branchy))!.MetadataToken;
            var code = (await service.DecompileAsync(service.Open(Bytes), token, default))!.Code;
            Assert.Contains("switch", code);
            Assert.Contains("catch (InvalidOperationException)", code);
            Assert.DoesNotContain("IL_00", code);
        }

        [Fact]
        public async Task Unknown_ids_and_tokens_that_are_not_a_type_or_method_give_null()
        {
            var service = new DecompilerService();
            string id = service.Open(Bytes);
            Assert.Null(await service.DecompileAsync("0000000000000000", TypeToken, default));
            Assert.Null(await service.DecompileAsync(id, 0x06FFFFFF, default));                       // a method row that does not exist
            Assert.Null(await service.DecompileAsync(id, 0x04000001, default));                       // a field token
            Assert.Null(await service.DecompileAsync(id, 0, default));
        }

        [Fact]
        public async Task Only_a_few_assemblies_stay_open_and_the_least_recently_used_goes_first()
        {
            var now = DateTime.UtcNow;
            var service = new DecompilerService(() => now);
            string first = service.Open(Variant(1)); now = now.AddSeconds(1);
            string second = service.Open(Variant(2)); now = now.AddSeconds(1);
            service.Open(Variant(3)); now = now.AddSeconds(1);
            Assert.NotNull(await service.DecompileAsync(first, TypeToken, default));                  // touching it makes it the most recent
            now = now.AddSeconds(1);
            service.Open(Variant(4));                                                                  // a fourth: variant 2 is now the oldest
            Assert.NotNull(await service.DecompileAsync(first, TypeToken, default));
            Assert.Null(await service.DecompileAsync(second, TypeToken, default));                     // evicted: it was the least recently used
        }

        [Fact]
        public async Task An_assembly_that_is_not_used_for_a_while_expires()
        {
            var now = DateTime.UtcNow;
            var service = new DecompilerService(() => now);
            string id = service.Open(Bytes);
            Assert.NotNull(await service.DecompileAsync(id, TypeToken, default));
            now = now.AddMinutes(16);
            Assert.Null(await service.DecompileAsync(id, TypeToken, default));
        }

        [Fact]
        public void Something_that_is_not_an_assembly_is_rejected()
        {
            var service = new DecompilerService();
            Assert.ThrowsAny<Exception>(() => service.Open(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        }

        [Fact]
        public async Task Concurrent_requests_for_the_same_assembly_are_serialised_and_all_succeed()
        {
            var service = new DecompilerService();
            string id = service.Open(Bytes);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.DecompileAsync(id, MethodToken, default)));
            Assert.All(results, r => Assert.Contains("int Simple()", r!.Code));
        }
    }


}
