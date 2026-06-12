using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellycheck.Configuration;
using Jellyfin.Plugin.Jellycheck.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck.ScheduledTasks
{
    /// <summary>
    /// Daily scheduled task that checks all series and movies in Jellyfin.
    /// When all users have watched a title and the configurable grace period has
    /// elapsed (14 days for shows, 7 for movies by default), it:
    ///   1. Deletes the series/movie from Sonarr or Radarr (addImportExclusion=false — no blocklist).
    ///   2. Cleans up the matching request in Overseerr so the title is freely re-requestable.
    ///
    /// If a new episode appears while a show is in its grace period, the "fully-watched"
    /// timestamp is automatically cleared and the clock starts over.
    /// </summary>
    public class AutoDeleteWatchedTask : IScheduledTask
    {
        // ── IScheduledTask identity ──────────────────────────────────────────
        public string Name        => "Jellycheck: Auto-Delete Watched Media";
        public string Key         => "JellycheckAutoDeleteWatched";
        public string Description => "Deletes shows and movies that every user has watched, after a configurable grace period. Items with favourites are skipped. Nothing is blacklisted.";
        public string Category    => "Jellycheck";

        private readonly WatchedAnalysisService _analysis;
        private readonly WatchedStateStore       _stateStore;
        private readonly SonarrService           _sonarr;
        private readonly RadarrService           _radarr;
        private readonly OverseerrService        _overseerr;
        private readonly ILogger<AutoDeleteWatchedTask> _logger;

        public AutoDeleteWatchedTask(
            WatchedAnalysisService analysis,
            WatchedStateStore stateStore,
            SonarrService sonarr,
            RadarrService radarr,
            OverseerrService overseerr,
            ILogger<AutoDeleteWatchedTask> logger)
        {
            _analysis   = analysis;
            _stateStore = stateStore;
            _sonarr     = sonarr;
            _radarr     = radarr;
            _overseerr  = overseerr;
            _logger     = logger;
        }

        // ── Triggers ─────────────────────────────────────────────────────────

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run once per day at 03:00 — quiet hours
            yield return new TaskTriggerInfo
            {
#if JELLYFIN_10_11
                Type           = TaskTriggerInfoType.DailyTrigger,
#else
                Type           = "DailyTrigger",
#endif
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            };
        }

        // ── Main execution ───────────────────────────────────────────────────

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] Plugin configuration is null — skipping task.");
                return;
            }

            bool showsEnabled  = config.EnableAutoDelete;
            bool moviesEnabled = config.EnableMovieAutoDelete;

            if (!showsEnabled && !moviesEnabled)
            {
                _logger.LogInformation("[Jellycheck/AutoDelete] Auto-delete is disabled for both shows and movies. Task skipped.");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("[Jellycheck/AutoDelete] Task started. Shows={Shows}, Movies={Movies}",
                showsEnabled, moviesEnabled);

            double step = 0;

            // ── TV Shows ─────────────────────────────────────────────────────
            if (showsEnabled)
            {
                await ProcessSeriesAsync(config, cancellationToken).ConfigureAwait(false);
            }
            step = 50;
            progress.Report(step);
            cancellationToken.ThrowIfCancellationRequested();

            // ── Movies ───────────────────────────────────────────────────────
            if (moviesEnabled)
            {
                await ProcessMoviesAsync(config, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(100);
            _logger.LogInformation("[Jellycheck/AutoDelete] Task completed.");
        }

        // ── Series processing ────────────────────────────────────────────────

        private async Task ProcessSeriesAsync(PluginConfiguration config, CancellationToken ct)
        {
            var allSeries = _analysis.GetAllSeries();
            _logger.LogInformation("[Jellycheck/AutoDelete] Checking {Count} series...", allSeries.Count);

            foreach (var series in allSeries)
            {
                ct.ThrowIfCancellationRequested();
                if (series == null) continue;

                var name = series.Name ?? series.Id.ToString();

                // 1. Favourite protection
                if (config.SkipIfFavorited && _analysis.IsItemFavoritedByAnyUser(series.Id))
                {
                    _logger.LogDebug("[Jellycheck/AutoDelete] Skipping '{Name}' — favourited by a user.", name);
                    _stateStore.Remove(series.Id); // clear timestamp if it was set
                    continue;
                }

                // 2. Check if all users have watched all episodes
                bool fullyWatched = _analysis.IsSeriesFullyWatchedByAll(series.Id);

                if (!fullyWatched)
                {
                    // New episode appeared or someone hasn't watched — reset the clock
                    _stateStore.Remove(series.Id);
                    continue;
                }

                // 3. Grace period logic
                var daysSince = _stateStore.DaysSinceFullyWatched(series.Id);
                if (daysSince == null)
                {
                    // First time we see this as fully watched — start the clock
                    _stateStore.SetFullyWatchedNow(series.Id, name, WatchedItemKind.Series);
                    _logger.LogInformation("[Jellycheck/AutoDelete] '{Name}' fully watched by all users — grace period started ({Days}d).",
                        name, config.DeleteDelayDays);

                    // Maybe send a notification that deletion is coming
                    if (config.NotifyUsersBeforeDeletion)
                        NotifyUpcomingDeletion(name, config.DeleteDelayDays, "series");

                    continue;
                }

                if (daysSince < config.DeleteDelayDays)
                {
                    _logger.LogDebug("[Jellycheck/AutoDelete] '{Name}' in grace period ({Days:F1}/{Required}d).",
                        name, daysSince, config.DeleteDelayDays);

                    // Fire notification if within the notification window
                    if (config.NotifyUsersBeforeDeletion)
                    {
                        double daysLeft = config.DeleteDelayDays - daysSince.Value;
                        if (daysLeft <= config.NotifyDaysBeforeDeletion)
                            NotifyUpcomingDeletion(name, (int)Math.Ceiling(daysLeft), "series");
                    }
                    continue;
                }

                // 4. Grace period elapsed — DELETE
                _logger.LogInformation("[Jellycheck/AutoDelete] Deleting '{Name}' — fully watched {Days:F1} days ago.", name, daysSince);
                await DeleteSeriesAsync(config, series, name, ct).ConfigureAwait(false);
                _stateStore.Remove(series.Id);
            }
        }

        // ── Movie processing ─────────────────────────────────────────────────

        private async Task ProcessMoviesAsync(PluginConfiguration config, CancellationToken ct)
        {
            var allMovies = _analysis.GetAllMovies();
            _logger.LogInformation("[Jellycheck/AutoDelete] Checking {Count} movies...", allMovies.Count);

            foreach (var movie in allMovies)
            {
                ct.ThrowIfCancellationRequested();
                if (movie == null) continue;

                var name = movie.Name ?? movie.Id.ToString();

                // 1. Favourite protection
                if (config.SkipIfFavorited && _analysis.IsItemFavoritedByAnyUser(movie.Id))
                {
                    _logger.LogDebug("[Jellycheck/AutoDelete] Skipping movie '{Name}' — favourited.", name);
                    _stateStore.Remove(movie.Id);
                    continue;
                }

                // 2. All users watched?
                bool watched = _analysis.IsMovieWatchedByAll(movie.Id);
                if (!watched)
                {
                    _stateStore.Remove(movie.Id);
                    continue;
                }

                // 3. Grace period
                var daysSince = _stateStore.DaysSinceFullyWatched(movie.Id);
                if (daysSince == null)
                {
                    _stateStore.SetFullyWatchedNow(movie.Id, name, WatchedItemKind.Movie);
                    _logger.LogInformation("[Jellycheck/AutoDelete] Movie '{Name}' watched by all — grace period started ({Days}d).",
                        name, config.MovieDeleteDelayDays);

                    if (config.NotifyUsersBeforeDeletion)
                        NotifyUpcomingDeletion(name, config.MovieDeleteDelayDays, "movie");

                    continue;
                }

                if (daysSince < config.MovieDeleteDelayDays)
                {
                    _logger.LogDebug("[Jellycheck/AutoDelete] Movie '{Name}' in grace period ({Days:F1}/{Required}d).",
                        name, daysSince, config.MovieDeleteDelayDays);

                    if (config.NotifyUsersBeforeDeletion)
                    {
                        double daysLeft = config.MovieDeleteDelayDays - daysSince.Value;
                        if (daysLeft <= config.NotifyDaysBeforeDeletion)
                            NotifyUpcomingDeletion(name, (int)Math.Ceiling(daysLeft), "movie");
                    }
                    continue;
                }

                // 4. Delete
                _logger.LogInformation("[Jellycheck/AutoDelete] Deleting movie '{Name}' — watched {Days:F1} days ago.", name, daysSince);
                await DeleteMovieAsync(config, movie, name, ct).ConfigureAwait(false);
                _stateStore.Remove(movie.Id);
            }
        }

        // ── Deletion helpers ─────────────────────────────────────────────────

        private async Task DeleteSeriesAsync(
            PluginConfiguration config,
            MediaBrowser.Controller.Entities.BaseItem series,
            string name,
            CancellationToken ct)
        {
            var tvdbId = _analysis.GetTvdbId(series);
            if (string.IsNullOrEmpty(tvdbId))
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] '{Name}' has no TVDB ID — cannot find in Sonarr.", name);
                return;
            }

            if (string.IsNullOrWhiteSpace(config.SonarrUrl) || string.IsNullOrWhiteSpace(config.SonarrApiKey))
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] Sonarr URL/ApiKey not configured — skipping '{Name}'.", name);
                return;
            }

            var sonarrId = await _sonarr.FindSeriesIdByTvdbAsync(config.SonarrUrl, config.SonarrApiKey, tvdbId).ConfigureAwait(false);
            if (sonarrId == null)
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] '{Name}' (TVDB {TvdbId}) not found in Sonarr.", name, tvdbId);
                return;
            }

            bool deleted = await _sonarr.DeleteSeriesAsync(config.SonarrUrl, config.SonarrApiKey, sonarrId.Value).ConfigureAwait(false);
            if (!deleted) return;

            // Clean up Overseerr — use TMDB ID (Overseerr maps TV shows via TMDB)
            var tmdbId = _analysis.GetTmdbId(series);
            if (!string.IsNullOrEmpty(tmdbId) && !string.IsNullOrWhiteSpace(config.OverseerrUrl))
            {
                await CleanupOverseerrAsync(config, tmdbId, isTv: true, name).ConfigureAwait(false);
            }
        }

        private async Task DeleteMovieAsync(
            PluginConfiguration config,
            MediaBrowser.Controller.Entities.BaseItem movie,
            string name,
            CancellationToken ct)
        {
            var tmdbId = _analysis.GetTmdbId(movie);
            if (string.IsNullOrEmpty(tmdbId))
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] Movie '{Name}' has no TMDB ID — cannot find in Radarr.", name);
                return;
            }

            if (string.IsNullOrWhiteSpace(config.RadarrUrl) || string.IsNullOrWhiteSpace(config.RadarrApiKey))
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] Radarr URL/ApiKey not configured — skipping '{Name}'.", name);
                return;
            }

            var radarrId = await _radarr.FindMovieIdByTmdbAsync(config.RadarrUrl, config.RadarrApiKey, tmdbId).ConfigureAwait(false);
            if (radarrId == null)
            {
                _logger.LogWarning("[Jellycheck/AutoDelete] Movie '{Name}' (TMDB {TmdbId}) not found in Radarr.", name, tmdbId);
                return;
            }

            bool deleted = await _radarr.DeleteMovieAsync(config.RadarrUrl, config.RadarrApiKey, radarrId.Value).ConfigureAwait(false);
            if (!deleted) return;

            if (!string.IsNullOrWhiteSpace(config.OverseerrUrl))
                await CleanupOverseerrAsync(config, tmdbId, isTv: false, name).ConfigureAwait(false);
        }

        private async Task CleanupOverseerrAsync(PluginConfiguration config, string tmdbId, bool isTv, string name)
        {
            if (string.IsNullOrWhiteSpace(config.OverseerrApiKey))
            {
                _logger.LogWarning("[Jellycheck/Overseerr] API key not configured — skipping Overseerr cleanup for '{Name}'.", name);
                return;
            }

            var requestId = await _overseerr.FindRequestIdByTmdbAsync(
                config.OverseerrUrl, config.OverseerrApiKey, tmdbId, isTv).ConfigureAwait(false);

            if (requestId == null)
            {
                _logger.LogInformation("[Jellycheck/Overseerr] No request found for '{Name}' — nothing to clean up.", name);
                return;
            }

            await _overseerr.DeleteRequestAsync(config.OverseerrUrl, config.OverseerrApiKey, requestId.Value).ConfigureAwait(false);
        }

        private void NotifyUpcomingDeletion(string itemName, int daysLeft, string kind)
        {
            // Jellyfin does not expose a simple "send notification to all users" API
            // from a scheduled task without the INotificationManager being injected.
            // For now we log a prominent message; full notification injection can be
            // added as a follow-up once the feature is verified working.
            _logger.LogWarning(
                "[Jellycheck/AutoDelete] UPCOMING DELETION: {Kind} '{Name}' will be deleted in {Days} day(s).",
                kind, itemName, daysLeft);
        }
    }
}
