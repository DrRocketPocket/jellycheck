using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck.Services
{
    /// <summary>
    /// Thin HTTP client wrapper for the Overseerr/Jellyseerr v1 API.
    /// After Sonarr/Radarr delete a title, this cleans up the Overseerr
    /// request record so the item shows as "not requested" and can be
    /// re-requested freely in the future.
    /// </summary>
    public class OverseerrService
    {
        private readonly ILogger<OverseerrService> _logger;
        private readonly HttpClient _http;

        public OverseerrService(ILogger<OverseerrService> logger)
        {
            _logger = logger;
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        // ── Request lookup ───────────────────────────────────────────────────

        /// <summary>
        /// Searches Overseerr for an approved/available request matching the
        /// given TMDB ID (works for both movies and TV shows — Overseerr uses TMDB for both).
        /// Returns the first matching request ID, or null if none found.
        /// </summary>
        public async Task<int?> FindRequestIdByTmdbAsync(string baseUrl, string apiKey, string tmdbId, bool isTv)
        {
            try
            {
                // Page through requests (Overseerr paginates)
                int page  = 1;
                int pages = 1;

                while (page <= pages)
                {
                    var url = $"{baseUrl.TrimEnd('/')}/api/v1/request?take=50&skip={(page - 1) * 50}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("X-Api-Key", apiKey);

                    using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("[Jellycheck/Overseerr] GET /request returned {Status}", resp.StatusCode);
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Calculate total pages from pageInfo
                    if (root.TryGetProperty("pageInfo", out var pi))
                    {
                        if (pi.TryGetProperty("pages", out var pagesEl))
                            pages = pagesEl.GetInt32();
                    }

                    if (!root.TryGetProperty("results", out var results)) break;

                    foreach (var item in results.EnumerateArray())
                    {
                        // Check media type
                        if (item.TryGetProperty("type", out var typeProp))
                        {
                            var type = typeProp.GetString();
                            if (isTv && type != "tv")   { page++; continue; }
                            if (!isTv && type != "movie") { page++; continue; }
                        }

                        // Check TMDB ID on the nested media object
                        if (item.TryGetProperty("media", out var media))
                        {
                            if (media.TryGetProperty("tmdbId", out var tmdbProp))
                            {
                                var candidate = tmdbProp.GetInt32().ToString();
                                if (candidate == tmdbId && item.TryGetProperty("id", out var idProp))
                                    return idProp.GetInt32();
                            }
                        }
                    }

                    page++;
                }

                _logger.LogWarning("[Jellycheck/Overseerr] No request found for TMDB ID {TmdbId} (tv={IsTv})", tmdbId, isTv);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jellycheck/Overseerr] Error finding request for TMDB ID {TmdbId}", tmdbId);
                return null;
            }
        }

        // ── Deletion ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes the Overseerr request by request ID.
        /// This does NOT blacklist the item — it simply clears the request
        /// so the item can be freely re-requested in the future.
        /// </summary>
        public async Task<bool> DeleteRequestAsync(string baseUrl, string apiKey, int requestId)
        {
            try
            {
                var url = $"{baseUrl.TrimEnd('/')}/api/v1/request/{requestId}";
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.Add("X-Api-Key", apiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Jellycheck/Overseerr] Deleted request ID {Id}", requestId);
                    return true;
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning("[Jellycheck/Overseerr] Delete request {Id} returned {Status}: {Body}", requestId, resp.StatusCode, body);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Jellycheck/Overseerr] Error deleting request ID {Id}", requestId);
                return false;
            }
        }
    }
}
