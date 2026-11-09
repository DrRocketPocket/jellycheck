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
    /// Thin HTTP client wrapper for the Radarr v3/v4 API.
    /// Supports movie lookup, deletion, and disk space inspection.
    /// </summary>
    public class RadarrService
    {
        private readonly ILogger<RadarrService>? _logger;
        private readonly HttpClient _http;

        public RadarrService(ILogger<RadarrService>? logger = null)
        {
            _logger = logger;
            
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
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
            return clean;
        }

        // ── Disk space ────────────────────────────────────────────────────────

        /// <summary>
        /// Retrieves the disk space list from Radarr /api/v3/diskspace or /api/v3/rootfolder.
        /// Returns null if Radarr is unreachable.
        /// </summary>
        public async Task<List<DiskSpaceItem>?> GetDiskSpaceAsync(string baseUrl, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
                return null;

            // 1. Try /api/v3/diskspace
            var disks = await FetchDisksFromEndpointAsync(baseUrl, apiKey, "api/v3/diskspace").ConfigureAwait(false);
            if (disks != null && disks.Count > 0)
            {
                return disks;
            }

            // 2. Fallback to /api/v3/rootfolder (reliable on seedboxes like Ultra.cc where media roots have quotas)
            var rootFolders = await FetchDisksFromEndpointAsync(baseUrl, apiKey, "api/v3/rootfolder").ConfigureAwait(false);
            if (rootFolders != null && rootFolders.Count > 0)
            {
                return rootFolders;
            }

            return null;
        }

        private async Task<List<DiskSpaceItem>?> FetchDisksFromEndpointAsync(string baseUrl, string apiKey, string endpoint)
        {
            try
            {
                var cleanBase = NormalizeBaseUrl(baseUrl);
                if (string.IsNullOrEmpty(cleanBase)) return null;

                var cleanKey = apiKey.Trim();
                var sep = endpoint.Contains('?') ? '&' : '?';
                var url = $"{cleanBase}/{endpoint}{sep}apikey={Uri.EscapeDataString(cleanKey)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Api-Key", cleanKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[Jellycheck/Radarr] GET {Endpoint} returned {Status}", endpoint, resp.StatusCode);
                    return null;
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

                return list.Count > 0 ? list : null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Jellycheck/Radarr] Error querying {Endpoint} from Radarr", endpoint);
                return null;
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

        // ── Movie lookup ─────────────────────────────────────────────────────

        /// <summary>
        /// Finds the Radarr internal movie ID for a given TMDB ID.
        /// Returns null if not found or if Radarr is unreachable.
        /// </summary>
        public async Task<int?> FindMovieIdByTmdbAsync(string baseUrl, string apiKey, string tmdbId)
        {
            try
            {
                var cleanBase = NormalizeBaseUrl(baseUrl);
                if (string.IsNullOrEmpty(cleanBase)) return null;

                var cleanKey = apiKey.Trim();
                var url = $"{cleanBase}/api/v3/movie?tmdbId={tmdbId}&apikey={Uri.EscapeDataString(cleanKey)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Api-Key", cleanKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[Jellycheck/Radarr] GET /movie?tmdbId={TmdbId} returned {Status}", tmdbId, resp.StatusCode);
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

                _logger?.LogWarning("[Jellycheck/Radarr] No movie found for TMDB ID {TmdbId}", tmdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Jellycheck/Radarr] Error finding movie for TMDB ID {TmdbId}", tmdbId);
                return null;
            }
        }

        // ── Deletion ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes a movie and its files from Radarr.
        /// addImportExclusion=false ensures nothing is blocklisted.
        /// </summary>
        public async Task<bool> DeleteMovieAsync(string baseUrl, string apiKey, int radarrId)
        {
            try
            {
                var cleanBase = NormalizeBaseUrl(baseUrl);
                if (string.IsNullOrEmpty(cleanBase)) return false;

                var cleanKey = apiKey.Trim();
                var url = $"{cleanBase}/api/v3/movie/{radarrId}?deleteFiles=true&addImportExclusion=false&apikey={Uri.EscapeDataString(cleanKey)}";

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.TryAddWithoutValidation("X-Api-Key", cleanKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger?.LogInformation("[Jellycheck/Radarr] Deleted movie ID {Id} (deleteFiles=true, addImportExclusion=false)", radarrId);
                    return true;
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger?.LogWarning("[Jellycheck/Radarr] Delete movie {Id} returned {Status}: {Body}", radarrId, resp.StatusCode, body);
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Jellycheck/Radarr] Error deleting movie ID {Id}", radarrId);
                return false;
            }
        }
    }
}
