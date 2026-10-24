using System.Text.Json;
using DotNetVisualizer.Web.Models;
using DotNetVisualizer.Web.Services.Vulnerabilities;

namespace DotNetVisualizer.Web.Tests
{
    /// <summary>OSV records shaped like the real api.osv.dev responses (trimmed), plus a scriptable fake database.</summary>
    public static class Fixtures
    {
        // GHSA-8g4q-xg66-9fp4 / CVE-2024-43485 (System.Text.Json): CVSS 3.1 7.5 High, fixed in 8.0.5 and 6.0.10
        public const string SystemTextJson43485 = """
        {
          "id": "GHSA-8g4q-xg66-9fp4",
          "summary": "Microsoft Security Advisory CVE-2024-43485 | .NET Denial of Service Vulnerability",
          "details": "Applications that deserialize into a model with [JsonExtensionData] can be attacked.",
          "aliases": ["BIT-dotnet-2024-43485", "CVE-2024-43485"],
          "modified": "2024-10-24T16:37:32.380910Z",
          "published": "2024-10-08T20:25:19Z",
          "database_specific": { "cwe_ids": ["CWE-407"], "severity": "HIGH" },
          "severity": [
            { "type": "CVSS_V3", "score": "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H" },
            { "type": "CVSS_V4", "score": "CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:L/VI:L/VA:H/SC:N/SI:N/SA:N" }
          ],
          "references": [ { "type": "WEB", "url": "https://github.com/dotnet/announcements/issues/329" } ],
          "affected": [
            { "package": { "name": "System.Text.Json", "ecosystem": "NuGet" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "8.0.0" }, { "fixed": "8.0.5" } ] } ],
              "versions": ["8.0.0","8.0.1","8.0.2","8.0.3","8.0.4"] },
            { "package": { "name": "System.Text.Json", "ecosystem": "NuGet" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "6.0.0" }, { "fixed": "6.0.10" } ] } ] }
          ]
        }
        """;

        // Same flaw published under a second id with the same CVE alias (must be merged into one finding)
        public const string SystemTextJson43485Duplicate = """
        {
          "id": "BIT-dotnet-2024-43485",
          "summary": "duplicate record",
          "aliases": ["CVE-2024-43485"],
          "affected": [ { "package": { "name": "System.Text.Json", "ecosystem": "NuGet" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "8.0.0" }, { "fixed": "8.0.5" } ] } ] } ]
        }
        """;

        // CVE-2018-1285 (log4net): CVSS 3.1 9.8 Critical
        public const string Log4Net = """
        {
          "id": "GHSA-2cwj-8chv-9pp9",
          "summary": "XML External Entity attack in log4net",
          "aliases": ["CVE-2018-1285"],
          "database_specific": { "cwe_ids": ["CWE-611"], "severity": "CRITICAL" },
          "severity": [ { "type": "CVSS_V3", "score": "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H" } ],
          "affected": [ { "package": { "name": "log4net", "ecosystem": "NuGet" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "2.0.10" } ] } ] } ]
        }
        """;

        // Only a CVSS v4 vector + advisory rating: severity must come from the rating
        public const string V4Only = """
        {
          "id": "GHSA-v4v4-v4v4-v4v4",
          "summary": "v4 only advisory",
          "database_specific": { "severity": "MODERATE" },
          "severity": [ { "type": "CVSS_V4", "score": "CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:L/VI:N/VA:N/SC:N/SI:N/SA:N" } ],
          "affected": [ { "package": { "name": "log4net", "ecosystem": "NuGet" }, "versions": ["2.0.8"] } ]
        }
        """;

        // No severity information at all
        public const string NoSeverity = """
        {
          "id": "GHSA-none-none-none",
          "summary": "no severity published",
          "affected": [ { "package": { "name": "log4net", "ecosystem": "NuGet" }, "versions": ["2.0.8"] } ]
        }
        """;

        public const string Withdrawn = """
        {
          "id": "GHSA-gone-gone-gone",
          "summary": "retracted",
          "withdrawn": "2024-01-01T00:00:00Z",
          "affected": [ { "package": { "name": "log4net", "ecosystem": "NuGet" }, "versions": ["2.0.8"] } ]
        }
        """;

        public static JsonElement Json(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
    }

    /// <summary>Scriptable stand-in for OSV.dev.</summary>
    public sealed class FakeOsvClient : IOsvClient
    {
        /// <summary>"name@version" -> advisory ids. A missing key = no advisories. Set <see cref="Unreachable"/> to simulate an outage.</summary>
        public Dictionary<string, List<string>> Matches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Records { get; } = new();
        public HashSet<string> FailingComponents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Unreachable { get; set; }
        public List<ScanComponent> Queried { get; } = new();

        public Task<IReadOnlyList<IReadOnlyList<string>?>> QueryBatchAsync(IReadOnlyList<ScanComponent> components, CancellationToken ct)
        {
            Queried.AddRange(components);
            var result = new List<IReadOnlyList<string>?>();
            foreach (var c in components)
            {
                var key = $"{c.Name}@{c.Version}";
                if (Unreachable || FailingComponents.Contains(key)) result.Add(null);
                else result.Add(Matches.TryGetValue(key, out var ids) ? ids : new List<string>());
            }
            return Task.FromResult<IReadOnlyList<IReadOnlyList<string>?>>(result);
        }

        public Task<JsonElement?> GetVulnerabilityAsync(string id, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(Records.TryGetValue(id, out var text) ? Fixtures.Json(text) : null);
    }
}
