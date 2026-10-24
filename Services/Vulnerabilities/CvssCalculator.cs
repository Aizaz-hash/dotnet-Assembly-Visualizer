namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    /// <summary>Parses CVSS v3.x vectors, computes the base score (FIRST.org specification) and classifies severity.</summary>
    public static class CvssCalculator
    {
        public sealed record Result(string Version, double? BaseScore, IReadOnlyDictionary<string, string> Metrics);

        /// <summary>
        /// Parses "CVSS:3.1/AV:N/AC:L/..." . For v3.0 and v3.1 the base score is computed; for other versions
        /// (e.g. v4.0, which needs a much larger lookup model) the score is left null.
        /// </summary>
        public static Result? Parse(string? vector)
        {
            if (string.IsNullOrWhiteSpace(vector)) return null;
            var parts = vector.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].StartsWith("CVSS:", StringComparison.Ordinal)) return null;

            var version = parts[0].Substring(5);
            var metrics = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split(':', 2);
                if (kv.Length == 2) metrics[kv[0]] = kv[1];
            }

            double? score = null;
            if (version is "3.0" or "3.1") score = BaseScoreV3(metrics, version == "3.1");
            return new Result(version, score, metrics);
        }

        private static double? BaseScoreV3(IReadOnlyDictionary<string, string> m, bool v31)
        {
            if (!m.TryGetValue("AV", out var av) || !m.TryGetValue("AC", out var ac) || !m.TryGetValue("PR", out var pr) ||
                !m.TryGetValue("UI", out var ui) || !m.TryGetValue("S", out var s) ||
                !m.TryGetValue("C", out var c) || !m.TryGetValue("I", out var i) || !m.TryGetValue("A", out var a))
                return null;

            bool scopeChanged = s == "C";
            if (s != "U" && s != "C") return null;

            double? avW = av switch { "N" => 0.85, "A" => 0.62, "L" => 0.55, "P" => 0.2, _ => null };
            double? acW = ac switch { "L" => 0.77, "H" => 0.44, _ => null };
            double? prW = pr switch
            {
                "N" => 0.85,
                "L" => scopeChanged ? 0.68 : 0.62,
                "H" => scopeChanged ? 0.5 : 0.27,
                _ => null
            };
            double? uiW = ui switch { "N" => 0.85, "R" => 0.62, _ => null };
            double? cW = Cia(c), iW = Cia(i), aW = Cia(a);
            if (avW is null || acW is null || prW is null || uiW is null || cW is null || iW is null || aW is null) return null;

            double iss = 1 - (1 - cW.Value) * (1 - iW.Value) * (1 - aW.Value);
            double impact = scopeChanged
                ? 7.52 * (iss - 0.029) - 3.25 * Math.Pow(iss - 0.02, 15)
                : 6.42 * iss;
            double exploitability = 8.22 * avW.Value * acW.Value * prW.Value * uiW.Value;

            if (impact <= 0) return 0.0;
            double raw = scopeChanged ? Math.Min(1.08 * (impact + exploitability), 10) : Math.Min(impact + exploitability, 10);
            return v31 ? RoundUp31(raw) : Math.Ceiling(raw * 10) / 10;
        }

        private static double? Cia(string v) => v switch { "H" => 0.56, "L" => 0.22, "N" => 0.0, _ => null };

        // CVSS v3.1 "Roundup" (avoids floating point artefacts).
        private static double RoundUp31(double x)
        {
            long intInput = (long)Math.Round(x * 100000);
            return intInput % 10000 == 0 ? intInput / 100000.0 : (Math.Floor(intInput / 10000.0) + 1) / 10.0;
        }

        /// <summary>Qualitative rating from the CVSS v3 specification.</summary>
        public static string Severity(double score) => score switch
        {
            >= 9.0 => "Critical",
            >= 7.0 => "High",
            >= 4.0 => "Medium",
            > 0.0 => "Low",
            _ => "None"
        };

        /// <summary>Maps GitHub / OSV advisory ratings (LOW, MODERATE, HIGH, CRITICAL) to our scale.</summary>
        public static string FromLabel(string? label) => label?.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => "Critical",
            "HIGH" => "High",
            "MODERATE" or "MEDIUM" => "Medium",
            "LOW" => "Low",
            _ => "Unknown"
        };

        /// <summary>Plain-language impact statements derived from the vector metrics (no speculation beyond the vector).</summary>
        public static List<string> Describe(IReadOnlyDictionary<string, string> m)
        {
            var list = new List<string>();
            string Get(string k) => m.TryGetValue(k, out var v) ? v : "";

            list.Add(Get("AV") switch
            {
                "N" => "Attack vector: network - can be exploited remotely, without local or physical access.",
                "A" => "Attack vector: adjacent network - the attacker must be on the same network segment.",
                "L" => "Attack vector: local - requires access to the machine (or a local user's action).",
                "P" => "Attack vector: physical - requires physical access to the device.",
                _ => ""
            });
            list.Add(Get("AC") == "L"
                ? "Attack complexity: low - no special conditions are needed; it can be repeated at will."
                : Get("AC") == "H" ? "Attack complexity: high - needs conditions outside the attacker's control, which makes exploitation harder." : "");
            list.Add(Get("PR") switch
            {
                "N" => "Privileges required: none - no authentication is needed.",
                "L" => "Privileges required: low - a basic authenticated account is enough.",
                "H" => "Privileges required: high - administrative privileges are needed.",
                _ => ""
            });
            list.Add(Get("UI") == "N"
                ? "User interaction: none - no victim action is required."
                : Get("UI") == "R" ? "User interaction: required - a user must open, click or process attacker-supplied content." : "");

            string Level(string k) => Get(k) switch { "H" => "high", "L" => "low", "N" => "none", _ => "" };
            var c = Level("C"); var i = Level("I"); var a = Level("A");
            if (c != "" && i != "" && a != "")
                list.Add($"Impact - confidentiality: {c}, integrity: {i}, availability: {a}" +
                         (Get("S") == "C" ? "; the impact can extend beyond the vulnerable component (scope changed)." : "."));
            return list.Where(x => x.Length > 0).ToList();
        }
    }
}
