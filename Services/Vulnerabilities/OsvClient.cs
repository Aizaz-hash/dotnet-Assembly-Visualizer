using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNetVisualizer.Web.Models;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    public interface IOsvClient
    {
        /// <summary>Returns, for every input component (same order), the ids of advisories that affect it. A null entry = the query failed.</summary>
        Task<IReadOnlyList<IReadOnlyList<string>?>> QueryBatchAsync(IReadOnlyList<ScanComponent> components, CancellationToken ct);

        /// <summary>Full advisory record, or null when it could not be fetched.</summary>
        Task<JsonElement?> GetVulnerabilityAsync(string id, CancellationToken ct);
    }

    /// <summary>Client for the OSV.dev API (https://osv.dev/docs/). Only package names and versions are sent.</summary>
    public sealed class OsvClient : IOsvClient
    {
        private const int BatchSize = 500;                                  // OSV allows up to 1000 queries per call
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, JsonElement> _cache = new();
        private readonly SemaphoreSlim _detailGate = new(6);

        public OsvClient(HttpClient http) => _http = http;

        public async Task<IReadOnlyList<IReadOnlyList<string>?>> QueryBatchAsync(IReadOnlyList<ScanComponent> components, CancellationToken ct)
        {
            var results = new IReadOnlyList<string>?[components.Count];

            for (int start = 0; start < components.Count; start += BatchSize)
            {
                var chunk = components.Skip(start).Take(BatchSize).ToList();
                var body = new
                {
                    queries = chunk.Select(c => new
                    {
                        package = new { name = c.Name, ecosystem = c.Ecosystem },
                        version = c.Version
                    })
                };

                try
                {
                    using var response = await _http.PostAsJsonAsync("v1/querybatch", body, ct);
                    response.EnsureSuccessStatusCode();
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                    var arr = doc.RootElement.GetProperty("results");
                    int i = 0;
                    foreach (var item in arr.EnumerateArray())
                    {
                        var ids = new List<string>();
                        if (item.TryGetProperty("vulns", out var vulns))
                            foreach (var v in vulns.EnumerateArray())
                                if (v.TryGetProperty("id", out var id) && id.GetString() is { } s) ids.Add(s);

                        // A result that carries a page token means "more advisories exist than were returned".
                        // Keep what we have; the scanner reports the component as incomplete via the marker below.
                        if (item.TryGetProperty("next_page_token", out _)) ids.Add("__more__");

                        if (start + i < results.Length) results[start + i] = ids;
                        i++;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    if (ct.IsCancellationRequested) throw;
                    // leave this chunk's entries null -> the scanner marks them as failed
                }
            }
            return results;
        }

        public async Task<JsonElement?> GetVulnerabilityAsync(string id, CancellationToken ct)
        {
            if (_cache.TryGetValue(id, out var cached)) return cached;

            await _detailGate.WaitAsync(ct);
            try
            {
                using var response = await _http.GetAsync($"v1/vulns/{Uri.EscapeDataString(id)}", ct);
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                var clone = doc.RootElement.Clone();
                _cache[id] = clone;
                return clone;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            finally
            {
                _detailGate.Release();
            }
        }
    }
}
