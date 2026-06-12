using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck.Services
{
    /// <summary>
    /// Thin HTTP client wrapper for the Radarr v3 API.
    /// Only the operations needed by the auto-delete feature are implemented.
    /// </summary>
    public class RadarrService
    {
        private readonly ILogger<RadarrService> _logger;
        private readonly HttpClient _http;

        public RadarrService(ILogger<RadarrService> logger)
        {
            _logger = logger;
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
                    _logger.LogWarning("[Jellycheck/Radarr] GET /movie?tmdbId={TmdbId} returned {Status}", tmdbId, resp.StatusCode);
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

                _logger.LogWarning("[Jellycheck/Radarr] No movie found for TMDB ID {TmdbId}", tmdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jellycheck/Radarr] Error finding movie for TMDB ID {TmdbId}", tmdbId);
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
                    _logger.LogInformation("[Jellycheck/Radarr] Deleted movie ID {Id} (deleteFiles=true, addImportExclusion=false)", radarrId);
                    return true;
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning("[Jellycheck/Radarr] Delete movie {Id} returned {Status}: {Body}", radarrId, resp.StatusCode, body);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jellycheck/Radarr] Error deleting movie ID {Id}", radarrId);
                return false;
            }
        }
    }
}
