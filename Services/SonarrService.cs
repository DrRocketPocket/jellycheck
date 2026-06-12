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
    /// Thin HTTP client wrapper for the Sonarr v3 API.
    /// Only the operations needed by the auto-delete feature are implemented.
    /// </summary>
    public class SonarrService
    {
        private readonly ILogger<SonarrService> _logger;
        private readonly HttpClient _http;

        public SonarrService(ILogger<SonarrService> logger)
        {
            _logger = logger;
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
                var url = $"{baseUrl.TrimEnd('/')}/api/v3/series?tvdbId={tvdbId}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Api-Key", apiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Jellycheck/Sonarr] GET /series?tvdbId={TvdbId} returned {Status}", tvdbId, resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Sonarr returns an array
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                    {
                        if (el.TryGetProperty("id", out var idProp))
                            return idProp.GetInt32();
                    }
                }
                // Some Sonarr versions return single object when queried by tvdbId
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var idProp))
                        return idProp.GetInt32();
                }

                _logger.LogWarning("[Jellycheck/Sonarr] No series found for TVDB ID {TvdbId}", tvdbId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jellycheck/Sonarr] Error finding series for TVDB ID {TvdbId}", tvdbId);
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
                var url = $"{baseUrl.TrimEnd('/')}/api/v3/series/{sonarrId}?deleteFiles=true&addImportExclusion=false";
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.Add("X-Api-Key", apiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Jellycheck/Sonarr] Deleted series ID {Id} (deleteFiles=true, addImportExclusion=false)", sonarrId);
                    return true;
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning("[Jellycheck/Sonarr] Delete series {Id} returned {Status}: {Body}", sonarrId, resp.StatusCode, body);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jellycheck/Sonarr] Error deleting series ID {Id}", sonarrId);
                return false;
            }
        }
    }
}
