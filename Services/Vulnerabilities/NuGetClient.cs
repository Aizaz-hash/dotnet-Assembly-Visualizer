using System.Collections.Concurrent;
using System.Xml.Linq;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    public sealed record NuspecDependency(string Id, string? VersionRange);
    public sealed record NuspecDependencyGroup(string? TargetFramework, IReadOnlyList<NuspecDependency> Dependencies);
    public sealed record NuspecInfo(string Id, string Version, IReadOnlyList<NuspecDependencyGroup> Groups);

    /// <summary>NuGet could not be reached (offline, blocked, timed out). Distinct from "the package does not exist".</summary>
    public sealed class NuGetUnavailableException : Exception
    {
        public NuGetUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public interface INuGetClient
    {
        /// <summary>All published versions of a package, or null when the package does not exist. Throws <see cref="NuGetUnavailableException"/> when NuGet cannot be reached.</summary>
        Task<IReadOnlyList<string>?> GetVersionsAsync(string packageId, CancellationToken ct);

        /// <summary>The package's declared dependencies, or null when the package/version does not exist. Throws <see cref="NuGetUnavailableException"/> when NuGet cannot be reached.</summary>
        Task<NuspecInfo?> GetNuspecAsync(string packageId, string version, CancellationToken ct);
    }

    /// <summary>
    /// Reads package metadata from the NuGet v3 "flat container" (versions index + .nuspec). Read-only; only package ids and
    /// versions are sent. Answers are cached for the life of the process (an outage is not cached).
    /// </summary>
    public sealed class NuGetClient : INuGetClient
    {
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>?>> _versions = new();
        private readonly ConcurrentDictionary<string, Task<NuspecInfo?>> _nuspecs = new();

        public NuGetClient(HttpClient http) => _http = http;

        public async Task<IReadOnlyList<string>?> GetVersionsAsync(string packageId, CancellationToken ct)
        {
            var key = packageId.ToLowerInvariant();
            var task = _versions.GetOrAdd(key, id => FetchVersions(id, CancellationToken.None));
            try { return await task.WaitAsync(ct); }
            catch (NuGetUnavailableException) { _versions.TryRemove(key, out _); throw; }
        }

        public async Task<NuspecInfo?> GetNuspecAsync(string packageId, string version, CancellationToken ct)
        {
            var key = packageId.ToLowerInvariant() + "/" + version.ToLowerInvariant();
            var task = _nuspecs.GetOrAdd(key, _ => FetchNuspec(packageId.ToLowerInvariant(), version.ToLowerInvariant(), CancellationToken.None));
            try { return await task.WaitAsync(ct); }
            catch (NuGetUnavailableException) { _nuspecs.TryRemove(key, out _); throw; }
        }

        private async Task<IReadOnlyList<string>?> FetchVersions(string id, CancellationToken ct)
        {
            try
            {
                using var response = await _http.GetAsync($"{Uri.EscapeDataString(id)}/index.json", ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                return doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new NuGetUnavailableException("NuGet could not be reached for " + id, ex);
            }
        }

        private async Task<NuspecInfo?> FetchNuspec(string id, string version, CancellationToken ct)
        {
            try
            {
                using var response = await _http.GetAsync($"{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(id)}.nuspec", ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                return NuspecParser.Parse(await response.Content.ReadAsStringAsync(ct), id, version);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
                throw new NuGetUnavailableException($"NuGet could not be reached for {id} {version}", ex);
            }
        }
    }

    public static class NuspecParser
    {
        public static NuspecInfo Parse(string xml, string fallbackId, string fallbackVersion)
        {
            var doc = XDocument.Parse(xml);
            // the nuspec namespace differs between schema versions, so match on local names only
            var meta = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string id = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim() ?? fallbackId;
            string version = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim() ?? fallbackVersion;

            var groups = new List<NuspecDependencyGroup>();
            var deps = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
            if (deps != null)
            {
                foreach (var group in deps.Elements().Where(e => e.Name.LocalName == "group"))
                    groups.Add(new NuspecDependencyGroup((string?)group.Attribute("targetFramework"), ReadDependencies(group)));

                // legacy nuspec: dependencies listed directly, valid for every framework
                var direct = ReadDependencies(deps);
                if (direct.Count > 0) groups.Add(new NuspecDependencyGroup(null, direct));
            }
            return new NuspecInfo(id, version, groups);
        }

        private static List<NuspecDependency> ReadDependencies(XElement parent) =>
            parent.Elements().Where(e => e.Name.LocalName == "dependency" && !string.IsNullOrWhiteSpace((string?)e.Attribute("id")))
                  .Select(e => new NuspecDependency(((string)e.Attribute("id")!).Trim(), (string?)e.Attribute("version")))
                  .ToList();
    }
}
