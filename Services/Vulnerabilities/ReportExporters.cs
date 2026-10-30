using System.Text.Json;
using System.Text.Json.Nodes;
using DotNetVisualizer.Web.Models;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    /// <summary>Turns a <see cref="ScanReport"/> into formats other tools read: a CycloneDX SBOM and SARIF for GitHub code scanning.</summary>
    public static class ReportExporters
    {
        public const string ToolName = "dotnet Assembly Visualizer";
        private const string ToolUrl = "https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer";
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

        public static string Serialize(JsonNode node) => node.ToJsonString(Pretty);

        public static string PackageUrl(string name, string version) =>
            $"pkg:nuget/{Uri.EscapeDataString(name)}@{Uri.EscapeDataString(version)}";

        // ---------------------------------------------------------------- CycloneDX 1.5

        /// <summary>
        /// A software bill of materials: every NuGet package that was identified with a version, who depends on whom (from the
        /// dependency path), and the vulnerabilities found. Dependencies that could not be resolved are not invented - they are
        /// simply absent, and the report's own notes say so.
        /// </summary>
        public static JsonNode CycloneDx(ScanReport report, string assemblyName)
        {
            var packages = report.Components
                .Where(c => c.Ecosystem == "NuGet" && !string.IsNullOrEmpty(c.Version) && c.Resolution == "resolved")
                .GroupBy(c => PackageUrl(c.Name, c.Version)).Select(g => g.First()).ToList();
            var byName = packages.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => PackageUrl(g.First().Name, g.First().Version), StringComparer.OrdinalIgnoreCase);

            const string rootRef = "root-component";
            var components = new JsonArray();
            var dependsOn = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal) { [rootRef] = new(StringComparer.Ordinal) };

            foreach (var p in packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                string purl = PackageUrl(p.Name, p.Version);
                var props = new JsonArray();
                if (p.Level != null) props.Add(Prop("dotnetvisualizer:dependency-level", p.Level.ToString()!));
                props.Add(Prop("dotnetvisualizer:version-source", p.VersionSource));
                if (!string.IsNullOrEmpty(p.Via)) props.Add(Prop("dotnetvisualizer:path", p.Via!));

                components.Add(new JsonObject
                {
                    ["type"] = "library", ["bom-ref"] = purl, ["name"] = p.Name, ["version"] = p.Version, ["purl"] = purl,
                    ["scope"] = "required", ["properties"] = props
                });

                // parent = the element before this package in "root → A → B → this"; no path (flat manifest) = a direct dependency of the root
                string parent = rootRef;
                var parts = (p.Via ?? "").Split(" → ", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && byName.TryGetValue(parts[^2], out var parentRef)) parent = parentRef;
                if (!dependsOn.TryGetValue(parent, out var set)) dependsOn[parent] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(purl);
                if (!dependsOn.ContainsKey(purl)) dependsOn[purl] = new SortedSet<string>(StringComparer.Ordinal);
            }

            var dependencies = new JsonArray();
            foreach (var (r, set) in dependsOn.OrderBy(k => k.Key == rootRef ? "" : k.Key, StringComparer.Ordinal))
                dependencies.Add(new JsonObject { ["ref"] = r, ["dependsOn"] = new JsonArray(set.Select(x => (JsonNode)x).ToArray()) });

            var vulns = new JsonArray();
            foreach (var f in report.Findings)
            {
                string purl = PackageUrl(f.Component, f.InstalledVersion);
                var rating = new JsonObject { ["source"] = new JsonObject { ["name"] = "OSV.dev" }, ["severity"] = f.Severity.ToLowerInvariant() };
                if (f.CvssScore != null) rating["score"] = f.CvssScore;
                if (f.CvssVector != null)
                {
                    rating["vector"] = f.CvssVector;
                    rating["method"] = f.CvssVector.StartsWith("CVSS:3.0") ? "CVSSv3" : f.CvssVector.StartsWith("CVSS:4") ? "CVSSv4" : "CVSSv31";
                }
                var v = new JsonObject
                {
                    ["id"] = f.Id,
                    ["source"] = new JsonObject { ["name"] = "OSV.dev", ["url"] = f.DatabaseUrl },
                    ["ratings"] = new JsonArray(rating),
                    ["description"] = f.Summary,
                    ["affects"] = new JsonArray(new JsonObject { ["ref"] = purl }),
                    ["properties"] = new JsonArray(Prop("dotnetvisualizer:confidence", f.Confidence))
                };
                if (f.Aliases.Count > 0)
                    v["references"] = new JsonArray(f.Aliases.Where(a => a != f.Id).Select(a => (JsonNode)new JsonObject { ["id"] = a, ["source"] = new JsonObject { ["name"] = "OSV.dev" } }).ToArray());
                var cwes = f.Cwes.Select(c => int.TryParse(c.Replace("CWE-", ""), out var n) ? n : (int?)null).Where(n => n != null).Select(n => (JsonNode)n!.Value).ToArray();
                if (cwes.Length > 0) v["cwes"] = new JsonArray(cwes);
                if (f.RecommendedVersion != null) v["recommendation"] = $"Upgrade {f.Component} to {f.RecommendedVersion} or later.";
                vulns.Add(v);
            }

            return new JsonObject
            {
                ["bomFormat"] = "CycloneDX",
                ["specVersion"] = "1.5",
                ["serialNumber"] = "urn:uuid:" + Guid.NewGuid(),
                ["version"] = 1,
                ["metadata"] = new JsonObject
                {
                    ["timestamp"] = report.ScannedAtUtc.ToString("o"),
                    ["tools"] = new JsonObject { ["components"] = new JsonArray(new JsonObject { ["type"] = "application", ["name"] = ToolName, ["externalReferences"] = new JsonArray(new JsonObject { ["type"] = "website", ["url"] = ToolUrl }) }) },
                    ["component"] = new JsonObject { ["type"] = "application", ["bom-ref"] = rootRef, ["name"] = assemblyName }
                },
                ["components"] = components,
                ["dependencies"] = dependencies,
                ["vulnerabilities"] = vulns
            };
        }

        // ---------------------------------------------------------------- SARIF 2.1.0

        /// <summary>SARIF for GitHub code scanning (upload with github/codeql-action/upload-sarif). One rule per advisory, one result per affected package.</summary>
        public static JsonNode Sarif(ScanReport report, string assemblyFile)
        {
            static string Level(string severity) => severity switch { "Critical" or "High" => "error", "Medium" => "warning", _ => "note" };
            // an informational finding must not break a pipeline or look like a confirmed error in code scanning
            static string ResultLevel(VulnerabilityFinding f) => f.Confidence == "informational" ? "note" : Level(f.Severity);

            var rules = new JsonArray();
            foreach (var f in report.Findings.GroupBy(f => f.Id).Select(g => g.First()))
            {
                var rule = new JsonObject
                {
                    ["id"] = f.Id, ["name"] = f.Id.Replace("-", ""),
                    ["shortDescription"] = new JsonObject { ["text"] = f.Summary.Length > 0 ? f.Summary : f.Id },
                    ["fullDescription"] = new JsonObject { ["text"] = string.IsNullOrWhiteSpace(f.Details) ? f.Summary : Trim(f.Details, 1000) },
                    ["helpUri"] = f.DatabaseUrl,
                    ["help"] = new JsonObject { ["text"] = string.Join("\n", f.Remediation), ["markdown"] = string.Join("\n\n", f.Remediation) },
                    ["defaultConfiguration"] = new JsonObject { ["level"] = Level(f.Severity) },
                    ["properties"] = new JsonObject { ["tags"] = new JsonArray("security", "vulnerability", "dependency") }
                };
                if (f.CvssScore != null) ((JsonObject)rule["properties"]!)["security-severity"] = f.CvssScore.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                rules.Add(rule);
            }

            var results = new JsonArray();
            foreach (var f in report.Findings)
            {
                string where = f.Level != null ? $" (dependency level {f.Level}{(string.IsNullOrEmpty(f.DependencyPath) ? "" : ": " + f.DependencyPath)})" : "";
                string fix = f.RecommendedVersion != null ? $" Upgrade to {f.RecommendedVersion} or later." : " No fixed version is listed.";
                results.Add(new JsonObject
                {
                    ["ruleId"] = f.Id,
                    ["level"] = ResultLevel(f),
                    ["message"] = new JsonObject { ["text"] = $"{f.Severity}: {f.Component} {f.InstalledVersion} - {f.Summary}{where}.{fix}" + (f.Confidence != "confirmed" ? " (" + f.Confidence + ": " + f.ConfidenceNote + ")" : "") },
                    ["locations"] = new JsonArray(new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = assemblyFile.Replace('\\', '/') } },
                        ["logicalLocations"] = new JsonArray(new JsonObject { ["name"] = $"{f.Component}@{f.InstalledVersion}", ["kind"] = "package" })
                    }),
                    ["partialFingerprints"] = new JsonObject { ["primaryLocationLineHash"] = $"{f.Id}|{f.Component}|{f.InstalledVersion}" },
                    ["properties"] = new JsonObject { ["confidence"] = f.Confidence, ["cvssScore"] = f.CvssScore }
                });
            }

            return new JsonObject
            {
                ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
                ["version"] = "2.1.0",
                ["runs"] = new JsonArray(new JsonObject
                {
                    ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = ToolName, ["informationUri"] = ToolUrl, ["rules"] = rules } },
                    ["results"] = results,
                    ["invocations"] = new JsonArray(new JsonObject
                    {
                        ["executionSuccessful"] = report.Complete,
                        ["toolExecutionNotifications"] = new JsonArray(report.Notes.Select(n => (JsonNode)new JsonObject { ["level"] = "note", ["message"] = new JsonObject { ["text"] = n } }).ToArray())
                    })
                })
            };
        }

        private static JsonObject Prop(string name, string value) => new() { ["name"] = name, ["value"] = value };
        private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
