using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck.Services
{
    /// <summary>
    /// Thin HTTP client wrapper for the Sonarr v3/v4 API.
    /// Supports series lookup, deletion, and disk space inspection.
    /// </summary>
    public class SonarrService
    {
        private readonly ILogger<SonarrService>? _logger;
        private readonly HttpClient _http;

        public SonarrService(ILogger<SonarrService>? logger = null)
        {
            _logger = logger;
            
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var clean = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(clean)) return string.Empty;
            if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                clean = "https://" + clean;
            }
            if (clean.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(0, clean.Length - "/api/v3".Length);
            }
            else if (clean.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(0, clean.Length - "/api".Length);
            }
            return clean.TrimEnd('/');
        }

        // ── Disk space ────────────────────────────────────────────────────────

        /// <summary>
        /// Retrieves the disk space list from Sonarr with diagnostic log information.
        /// </summary>
        public async Task<(List<DiskSpaceItem>? Disks, string Log)> GetDiskSpaceWithLogAsync(string baseUrl, string apiKey)
        {
            var cleanBase = NormalizeBaseUrl(baseUrl);
            if (string.IsNullOrEmpty(cleanBase))
                return (null, "Sonarr URL is empty or invalid.");

            var cleanKey = (apiKey ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(cleanKey))
                return (null, "Sonarr API key is empty.");

            // 1. Try /api/v3/diskspace
            var (disks, log1) = await FetchDisksWithLogAsync(cleanBase, cleanKey, "api/v3/diskspace").ConfigureAwait(false);
            if (disks != null && disks.Count > 0)
            {
                return (disks, $"Sonarr /api/v3/diskspace OK: {disks.Count} disk(s) found.");
            }

            // 2. Fallback to /api/v3/rootfolder (reliable on seedboxes like Ultra.cc)
            var (rootDisks, log2) = await FetchDisksWithLogAsync(cleanBase, cleanKey, "api/v3/rootfolder").ConfigureAwait(false);
            if (rootDisks != null && rootDisks.Count > 0)
            {
                return (rootDisks, $"Sonarr /api/v3/rootfolder OK: {rootDisks.Count} folder(s) found.");
            }

            return (null, $"Sonarr query failed. /diskspace: [{log1}], /rootfolder: [{log2}]");
        }

        public async Task<List<DiskSpaceItem>?> GetDiskSpaceAsync(string baseUrl, string apiKey)
        {
            var (disks, _) = await GetDiskSpaceWithLogAsync(baseUrl, apiKey).ConfigureAwait(false);
            return disks;
        }

        private async Task<(List<DiskSpaceItem>? Disks, string Log)> FetchDisksWithLogAsync(string cleanBase, string cleanKey, string endpoint)
        {
            try
            {
                var sep = endpoint.Contains('?') ? '&' : '?';
                var url = $"{cleanBase}/{endpoint}{sep}apikey={Uri.EscapeDataString(cleanKey)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Api-Key", cleanKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = string.Empty;
                    try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                    var shortBody = body.Length > 80 ? body.Substring(0, 80) + "..." : body;
                    return (null, $"HTTP {(int)resp.StatusCode} {resp.StatusCode} ({shortBody})");
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var list = new List<DiskSpaceItem>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        ParseDiskItem(el, list);
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in records.EnumerateArray())
                        {
                            ParseDiskItem(el, list);
                        }
                    }
                    else
                    {
                        ParseDiskItem(doc.RootElement, list);
                    }
                }

                if (list.Count > 0)
                {
                    return (list, $"OK ({list.Count} items)");
                }
                return (null, "Valid JSON received but no disks with TotalSpace > 0");
            }
            catch (Exception ex)
            {
                return (null, $"Connection error: {ex.Message}");
            }
        }

        private static void ParseDiskItem(JsonElement el, List<DiskSpaceItem> list)
        {
            string path = GetStringProp(el, "path") ?? "";
            string label = GetStringProp(el, "label") ?? "";
            long free = GetInt64Prop(el, "freeSpace", "freespace", "free_space");
            long total = GetInt64Prop(el, "totalSpace", "totalspace", "total_space");

            if (total > 0)
            {
                list.Add(new DiskSpaceItem
                {
                    Path = path,
                    Label = label,
                    FreeSpace = free,
                    TotalSpace = total
                });
            }
        }

        private static string? GetStringProp(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p))
                return p.GetString();
            foreach (var prop in el.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value.GetString();
            }
            return null;
        }

        private static long GetInt64Prop(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (el.TryGetProperty(name, out var p))
                {
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return (long)d;
                    if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
                }
            }
            foreach (var prop in el.EnumerateObject())
            {
                if (names.Any(n => string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase)))
                {
                    var p = prop.Value;
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return (long)d;
                    if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
                }
            }
            return 0;
        }

        // ── Series lookup ────────────────────────────────────────────────────

        /// <summary>
        /// Finds the Sonarr internal series ID for a given TVDB ID.
        /// Returns null if not found or if Sonarr is unreachable.
        /// </summary>
        public async Task<int?> FindSeriesIdByTvdbAsync(string baseUrl, string apiKey, string tvdbId)
        {
            try
            {
                var cleanBase = NormalizeBaseUrl(baseUrl);
                if (string.IsNullOrEmpty(cleanBase)) return null;

                var cleanKey = apiKey.Trim();
                var url = $"{cleanBase}/api/v3/series?tvdbId={tvdbId}&apikey={Uri.EscapeDataString(cleanKey)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Api-Key", cleanKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[Jellycheck/Sonarr] GET /series?tvdbId={TvdbId} returned {Status}", tvdbId, resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                    {
                        if (el.TryGetProperty("id", out var idProp))
                            return idProp.GetInt32();
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var idProp))
                        return idProp.GetInt32();
                }

                _logger?.LogWarning("[Jellycheck/Sonarr] No series found for TVDB ID {TvdbId}", tvdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Jellycheck/Sonarr] Error finding series for TVDB ID {TvdbId}", tvdbId);
                return null;
            }
        }

        // ── Deletion ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes a series and its files from Sonarr.
        /// addImportExclusion=false ensures nothing is blocklisted.
        /// </summary>
        public async Task<bool> DeleteSeriesAsync(string baseUrl, string apiKey, int sonarrId)
        {
            try
            {
                var cleanBase = NormalizeBaseUrl(baseUrl);
                if (string.IsNullOrEmpty(cleanBase)) return false;

                var cleanKey = apiKey.Trim();
                var url = $"{cleanBase}/api/v3/series/{sonarrId}?deleteFiles=true&addImportExclusion=false&apikey={Uri.EscapeDataString(cleanKey)}";

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.TryAddWithoutValidation("X-Api-Key", cleanKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger?.LogInformation("[Jellycheck/Sonarr] Deleted series ID {Id} (deleteFiles=true, addImportExclusion=false)", sonarrId);
                    return true;
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger?.LogWarning("[Jellycheck/Sonarr] Delete series {Id} returned {Status}: {Body}", sonarrId, resp.StatusCode, body);
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Jellycheck/Sonarr] Error deleting series ID {Id}", sonarrId);
                return false;
            }
        }
    }
}
