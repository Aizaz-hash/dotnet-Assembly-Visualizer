using System.Text.RegularExpressions;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    /// <summary>NuGet version-range and target-framework helpers used when walking package dependencies.</summary>
    public static class NuGetVersioning
    {
        /// <summary>
        /// The lowest version a NuGet range accepts (what restore picks when nothing else raises it):
        /// "1.2.3", "[1.2.3, )" or "(1.2.3, 2.0)" -> "1.2.3"; "(, 2.0]" and "" have no lower bound -> null.
        /// </summary>
        public static string? MinimumVersion(string? range)
        {
            if (string.IsNullOrWhiteSpace(range)) return null;
            var r = range.Trim();
            if (r[0] != '[' && r[0] != '(') return r;                       // "1.2.3" means ">= 1.2.3"
            var inner = r.Trim('[', ']', '(', ')');
            var lower = inner.Split(',')[0].Trim();
            return lower.Length == 0 ? null : lower;
        }

        public static bool IsStable(string version) => !version.Contains('-');

        public static bool TryParseNumeric(string version, out int[] parts)
        {
            var core = version.Split('-', '+')[0];
            var pieces = core.Split('.');
            parts = new int[4];
            if (pieces.Length == 0 || pieces.Length > 4) return false;
            for (int i = 0; i < pieces.Length; i++)
                if (!int.TryParse(pieces[i], out parts[i])) return false;
            return true;
        }

        /// <summary>
        /// Assembly versions (12.0.0.0) are not package versions (12.0.1). Without a manifest the best guess is the lowest stable
        /// package version that is not older than the assembly version and shares its major.minor; failing that, its major.
        /// </summary>
        public static string? EstimatePackageVersion(IEnumerable<string> published, string assemblyVersion)
        {
            if (!TryParseNumeric(assemblyVersion, out var a)) return null;
            var stable = published.Where(IsStable).Where(v => TryParseNumeric(v, out _))
                                  .OrderBy(v => v, Comparer<string>.Create(VersionComparer.Compare)).ToList();

            bool NotOlder(int[] v)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (v[i] > a[i]) return true;
                    if (v[i] < a[i]) return false;
                }
                return true;
            }

            foreach (var v in stable)
            {
                TryParseNumeric(v, out var p);
                if (p[0] == a[0] && p[1] == a[1] && NotOlder(p)) return v;
            }
            foreach (var v in stable)
            {
                TryParseNumeric(v, out var p);
                if (p[0] == a[0] && NotOlder(p)) return v;
            }
            return null;
        }

        // ---- target frameworks ---------------------------------------------------------------------------------------

        public sealed record Tfm(string Family, Version Version);

        private static readonly Regex Moniker = new(@"^(?<name>[a-z]+)(?<ver>\d[\d.]*)?(?:[-+].*)?$", RegexOptions.Compiled);

        /// <summary>Parses "net8.0", "netstandard2.0", "netcoreapp3.1", "net462", ".NETStandard2.0" or ".NETCoreApp,Version=v8.0".</summary>
        public static Tfm? ParseTfm(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim();

            var comma = Regex.Match(s, @"^(?<n>[^,]+),\s*Version=v(?<v>[\d.]+)", RegexOptions.IgnoreCase);
            if (comma.Success)
            {
                var n = comma.Groups["n"].Value.TrimStart('.').ToLowerInvariant();
                if (!Version.TryParse(Normalize(comma.Groups["v"].Value), out var ver)) return null;
                return n switch
                {
                    "netcoreapp" => ver.Major >= 5 ? new Tfm("net", ver) : new Tfm("netcoreapp", ver),
                    "netstandard" => new Tfm("netstandard", ver),
                    "netframework" => new Tfm("netfx", ver),
                    _ => null
                };
            }

            // ".NETStandard2.0" -> "netstandard2.0"
            var m = Moniker.Match(s.TrimStart('.').ToLowerInvariant());
            if (!m.Success) return null;
            string name = m.Groups["name"].Value, v2 = m.Groups["ver"].Value;
            if (v2.Length == 0) return null;

            if (name == "netstandard" || name == "netcoreapp")
                return Version.TryParse(Normalize(v2), out var ver2) ? new Tfm(name, ver2) : null;

            if (name == "net")
            {
                if (v2.Contains('.'))
                    return Version.TryParse(Normalize(v2), out var modern) ? new Tfm("net", modern) : null;
                var digits = v2.PadRight(3, '0');                            // net462 / net48 = .NET Framework
                return Version.TryParse($"{digits[0]}.{digits[1]}.{digits.Substring(2)}", out var fx) ? new Tfm("netfx", fx) : null;
            }
            return null;
        }

        private static string Normalize(string v) => v.Contains('.') ? v : v + ".0";

        /// <summary>True when the framework is modern .NET (5+) or unknown, where the shared framework supplies most System.* assemblies.</summary>
        public static bool IsModernDotNet(Tfm? tfm) => tfm == null || tfm.Family == "net";

        /// <summary>Picks the dependency group NuGet restore would use for the framework (no framework = the newest sensible group).</summary>
        public static NuspecDependencyGroup? SelectGroup(IReadOnlyList<NuspecDependencyGroup> groups, string? targetFramework)
        {
            if (groups.Count == 0) return null;
            var typed = groups.Select(g => (g, tfm: ParseTfm(g.TargetFramework))).Where(p => p.tfm != null).Select(p => (p.g, tfm: p.tfm!)).ToList();
            var target = ParseTfm(targetFramework);

            static int Rank(Tfm t) => t.Family switch { "net" => 4, "netcoreapp" => 3, "netstandard" => 2, _ => 1 };

            if (target != null)
            {
                var same = typed.Where(p => p.tfm.Family == target.Family && p.tfm.Version <= target.Version).OrderByDescending(p => p.tfm.Version).FirstOrDefault();
                if (same.g != null) return same.g;

                // a newer framework can consume older families: net -> netcoreapp -> netstandard; netfx -> netstandard
                var fallbacks = target.Family switch
                {
                    "net" => new[] { "netcoreapp", "netstandard" },
                    "netcoreapp" => new[] { "netstandard" },
                    "netfx" => new[] { "netstandard" },
                    _ => Array.Empty<string>()
                };
                foreach (var family in fallbacks)
                {
                    var pick = typed.Where(p => p.tfm.Family == family).OrderByDescending(p => p.tfm.Version).FirstOrDefault();
                    if (pick.g != null) return pick.g;
                }
            }
            else
            {
                var best = typed.OrderByDescending(p => Rank(p.tfm)).ThenByDescending(p => p.tfm.Version).FirstOrDefault();
                if (best.g != null) return best.g;
            }
            return groups.FirstOrDefault(g => g.TargetFramework == null) ?? groups[0];
        }
    }
}
