using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
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
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        // ── Disk space ────────────────────────────────────────────────────────

        /// <summary>
        /// Retrieves the disk space list from Radarr /api/v3/diskspace.
        /// Returns null if Radarr is unreachable.
        /// </summary>
        public async Task<List<DiskSpaceItem>?> GetDiskSpaceAsync(string baseUrl, string apiKey)
        {
            try
            {
                var url = $"{baseUrl.TrimEnd('/')}/api/v3/diskspace";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Api-Key", apiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[Jellycheck/Radarr] GET /api/v3/diskspace returned {Status}", resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var list = new List<DiskSpaceItem>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var path = el.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    var label = el.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    long free = el.TryGetProperty("freeSpace", out var f) ? f.GetInt64() : 0;
                    long total = el.TryGetProperty("totalSpace", out var t) ? t.GetInt64() : 0;

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
                return list;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Jellycheck/Radarr] Error querying disk space from Radarr");
                return null;
            }
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
                var url = $"{baseUrl.TrimEnd('/')}/api/v3/movie?tmdbId={tmdbId}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Api-Key", apiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[Jellycheck/Radarr] GET /movie?tmdbId={TmdbId} returned {Status}", tmdbId, resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Radarr returns an array when filtering by tmdbId
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
                var url = $"{baseUrl.TrimEnd('/')}/api/v3/movie/{radarrId}?deleteFiles=true&addImportExclusion=false";
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.Add("X-Api-Key", apiKey);

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
