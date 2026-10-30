using DotNetVisualizer.Web.Services.Vulnerabilities;
using Xunit;

namespace DotNetVisualizer.Web.Tests.Unit
{
    public class CvssCalculatorTests
    {
        // Reference scores from the FIRST.org CVSS v3.x calculator / NVD
        [Theory]
        [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)]
        [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H", 7.5)]
        [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H", 10.0)]
        [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N", 6.1)]
        [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:L/I:N/A:N", 5.3)]
        [InlineData("CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:H", 7.8)]
        [InlineData("CVSS:3.0/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)]
        [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:N", 0.0)]
        public void Computes_the_published_base_score(string vector, double expected)
        {
            var result = CvssCalculator.Parse(vector);
            Assert.NotNull(result);
            Assert.Equal(expected, result!.BaseScore);
        }

        [Fact]
        public void Cvss4_vectors_are_recognised_but_not_scored()
        {
            var result = CvssCalculator.Parse("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N");
            Assert.NotNull(result);
            Assert.Equal("4.0", result!.Version);
            Assert.Null(result.BaseScore);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("AV:N/AC:L")]
        public void Garbage_input_is_rejected(string? vector) => Assert.Null(CvssCalculator.Parse(vector));

        [Fact]
        public void Incomplete_vectors_have_no_score()
        {
            var result = CvssCalculator.Parse("CVSS:3.1/AV:N/AC:L/PR:N");
            Assert.NotNull(result);
            Assert.Null(result!.BaseScore);
        }

        [Theory]
        [InlineData(10.0, "Critical")]
        [InlineData(9.0, "Critical")]
        [InlineData(8.9, "High")]
        [InlineData(7.0, "High")]
        [InlineData(6.9, "Medium")]
        [InlineData(4.0, "Medium")]
        [InlineData(3.9, "Low")]
        [InlineData(0.1, "Low")]
        [InlineData(0.0, "None")]
        public void Severity_bands_follow_the_cvss_specification(double score, string expected) =>
            Assert.Equal(expected, CvssCalculator.Severity(score));

        [Theory]
        [InlineData("CRITICAL", "Critical")]
        [InlineData("high", "High")]
        [InlineData("MODERATE", "Medium")]
        [InlineData("LOW", "Low")]
        [InlineData("", "Unknown")]
        [InlineData(null, "Unknown")]
        [InlineData("whatever", "Unknown")]
        public void Advisory_labels_map_to_our_scale(string? label, string expected) =>
            Assert.Equal(expected, CvssCalculator.FromLabel(label));

        [Fact]
        public void Description_is_derived_only_from_the_vector()
        {
            var parsed = CvssCalculator.Parse("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H")!;
            var text = string.Join("\n", CvssCalculator.Describe(parsed.Metrics));
            Assert.Contains("network", text);
            Assert.Contains("none - no authentication", text);
            Assert.Contains("confidentiality: high, integrity: high, availability: high", text);
        }
    }

    public class VersionComparerTests
    {
        [Theory]
        [InlineData("1.0.0", "1.0.0", 0)]
        [InlineData("1.0.0", "1.0", 0)]
        [InlineData("1.2.3", "1.2.10", -1)]
        [InlineData("2.0.0", "1.9.9", 1)]
        [InlineData("8.0.0", "8.0.0-rc.1", 1)]
        [InlineData("8.0.0-beta", "8.0.0-rc", -1)]
        [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
        [InlineData("1.0.0+build5", "1.0.0", 0)]
        [InlineData("10.0.9", "8.0.5", 1)]
        public void Orders_versions_like_semver(string a, string b, int expectedSign) =>
            Assert.Equal(expectedSign, Math.Sign(VersionComparer.Compare(a, b)));

        [Theory]
        [InlineData("8.0.0", true)]
        [InlineData("8.0.4", true)]
        [InlineData("8.0.5", false)]       // the fixed version itself
        [InlineData("10.0.9", false)]
        [InlineData("7.0.0", false)]       // below "introduced" of the first range
        [InlineData("6.0.9", true)]        // inside the second range (no explicit versions list)
        [InlineData("6.0.10", false)]
        public void Evaluates_affected_ranges(string version, bool expected)
        {
            var record = Fixtures.Json(Fixtures.SystemTextJson43485);
            bool affected = record.GetProperty("affected").EnumerateArray().Any(a => VersionComparer.IsAffected(a, version));
            Assert.Equal(expected, affected);
        }

        [Fact]
        public void Open_ended_ranges_mean_never_fixed()
        {
            var a = Fixtures.Json("""{ "ranges": [ { "type": "SEMVER", "events": [ { "introduced": "1.0.0" } ] } ] }""");
            Assert.True(VersionComparer.IsAffected(a, "99.0.0"));
            Assert.False(VersionComparer.IsAffected(a, "0.9.0"));
        }

        [Fact]
        public void Last_affected_is_inclusive()
        {
            var a = Fixtures.Json("""{ "ranges": [ { "type": "SEMVER", "events": [ { "introduced": "0" }, { "last_affected": "2.0.0" } ] } ] }""");
            Assert.True(VersionComparer.IsAffected(a, "2.0.0"));
            Assert.False(VersionComparer.IsAffected(a, "2.0.1"));
        }

        [Fact]
        public void Git_commit_ranges_are_ignored()
        {
            var a = Fixtures.Json("""{ "ranges": [ { "type": "GIT", "events": [ { "introduced": "0" } ] } ] }""");
            Assert.False(VersionComparer.IsAffected(a, "1.0.0"));
        }

        [Fact]
        public void Lists_fixed_versions_in_ascending_order()
        {
            var affected = Fixtures.Json(Fixtures.SystemTextJson43485).GetProperty("affected");
            var all = affected.EnumerateArray().SelectMany(VersionComparer.FixedVersions).ToList();
            Assert.Equal(new[] { "6.0.10", "8.0.5" }, all.OrderBy(x => x, Comparer<string>.Create(VersionComparer.Compare)).ToArray());
        }
    }
}
