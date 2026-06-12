using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck.Services
{
    /// <summary>
    /// Shared service for analysing watched state across all Jellyfin users.
    /// Extracted from WatchedStatusController so both the overlay API and the
    /// auto-delete scheduled task can reuse the same logic.
    /// </summary>
    public class WatchedAnalysisService
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger<WatchedAnalysisService> _logger;

        public WatchedAnalysisService(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            ILogger<WatchedAnalysisService> logger)
        {
            _userManager     = userManager;
            _libraryManager  = libraryManager;
            _userDataManager = userDataManager;
            _logger          = logger;
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns a map of ItemId → list of users who have watched that item.
        /// Includes direct episodes/movies AND whole-series/season rollups.
        /// </summary>
        public Dictionary<Guid, List<WatchedUserInfo>> BuildWatchedMap()
        {
            var watchedMap = new Dictionary<Guid, List<WatchedUserInfo>>();
            var users = GetAllUsers().ToList();

            foreach (var user in users)
            {
                if (user == null) continue;
                var userId   = GetUserId(user);
                var username = GetUsername(user);
                if (userId == Guid.Empty) continue;

                var query = new InternalItemsQuery
                {
                    Recursive        = true,
                    IsPlayed         = true,
                    IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie }
                };
                SetUserOnQuery(query, user);

                var playedItems       = _libraryManager.GetItemList(query);
                var playedEpisodeIds  = new HashSet<Guid>();
                var seriesToCheck     = new HashSet<Guid>();
                var seasonsToCheck    = new HashSet<Guid>();

                foreach (var item in playedItems)
                {
                    if (item == null) continue;
                    AddWatched(watchedMap, item.Id, userId, username);

                    var seriesIdProp = item.GetType().GetProperty("SeriesId");
                    if (seriesIdProp?.GetValue(item) is Guid sid && sid != Guid.Empty)
                    {
                        playedEpisodeIds.Add(item.Id);
                        seriesToCheck.Add(sid);
                    }

                    var seasonIdProp = item.GetType().GetProperty("SeasonId");
                    if (seasonIdProp?.GetValue(item) is Guid snId && snId != Guid.Empty)
                        seasonsToCheck.Add(snId);
                }

                // Series rollup
                foreach (var seriesId in seriesToCheck)
                {
                    var series = _libraryManager.GetItemById(seriesId);
                    if (series == null) continue;

                    var epQuery = new InternalItemsQuery
                    {
                        Parent           = series,
                        Recursive        = true,
                        IncludeItemTypes = new[] { BaseItemKind.Episode }
                    };
                    SetUserOnQuery(epQuery, user);
                    var allEps = _libraryManager.GetItemList(epQuery);

                    if (allEps.Count > 0 && allEps.All(ep => ep != null && playedEpisodeIds.Contains(ep.Id)))
                        AddWatched(watchedMap, seriesId, userId, username);
                }

                // Season rollup
                foreach (var seasonId in seasonsToCheck)
                {
                    var season = _libraryManager.GetItemById(seasonId);
                    if (season == null) continue;

                    var epQuery = new InternalItemsQuery
                    {
                        Parent           = season,
                        Recursive        = true,
                        IncludeItemTypes = new[] { BaseItemKind.Episode }
                    };
                    SetUserOnQuery(epQuery, user);
                    var allEps = _libraryManager.GetItemList(epQuery);

                    if (allEps.Count > 0 && allEps.All(ep => ep != null && playedEpisodeIds.Contains(ep.Id)))
                        AddWatched(watchedMap, seasonId, userId, username);
                }
            }

            return watchedMap;
        }

        /// <summary>
        /// Returns true if ALL Jellyfin users have watched the given series (by seriesId).
        /// Uses a fresh per-user query so it is always up to date.
        /// </summary>
        public bool IsSeriesFullyWatchedByAll(Guid seriesId)
        {
            var series = _libraryManager.GetItemById(seriesId);
            if (series == null) return false;

            var users = GetAllUsers().ToList();
            if (users.Count == 0) return false;

            foreach (var user in users)
            {
                if (user == null) continue;
                if (!IsSeriesFullyWatchedByUser(series, user)) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if ALL Jellyfin users have watched the given movie (by movieId).
        /// </summary>
        public bool IsMovieWatchedByAll(Guid movieId)
        {
            var users = GetAllUsers().ToList();
            if (users.Count == 0) return false;

            foreach (var user in users)
            {
                if (user == null) continue;
                var userId = GetUserId(user);
                if (userId == Guid.Empty) continue;

                var query = new InternalItemsQuery
                {
                    Recursive        = true,
                    IsPlayed         = true,
                    ItemIds          = new[] { movieId },
                    IncludeItemTypes = new[] { BaseItemKind.Movie }
                };
                SetUserOnQuery(query, user);
                var result = _libraryManager.GetItemList(query);
                if (result.Count == 0) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if ANY user has the item marked as a favourite.
        /// </summary>
        public bool IsItemFavoritedByAnyUser(Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null) return false;

            foreach (var user in GetAllUsers())
            {
                if (user == null) continue;
                var userId = GetUserId(user);
                if (userId == Guid.Empty) continue;

                try
                {
                    // IUserDataManager.GetUserData requires a User object, not a Guid.
                    // We retrieve via reflection to stay version-compatible.
                    var getUserDataMethod = _userDataManager.GetType().GetMethod("GetUserData",
                        new[] { user.GetType(), typeof(BaseItem) });
                    if (getUserDataMethod != null)
                    {
                        var userData = getUserDataMethod.Invoke(_userDataManager, new object[] { user, item });
                        if (userData != null)
                        {
                            var isFavProp = userData.GetType().GetProperty("IsFavorite");
                            if (isFavProp?.GetValue(userData) is bool isFav && isFav) return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read user data for item {ItemId} / user {UserId}", itemId, userId);
                }
            }
            return false;
        }

        /// <summary>Returns all Series items in the library.</summary>
        public IReadOnlyList<BaseItem> GetAllSeries()
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive        = true,
                IncludeItemTypes = new[] { BaseItemKind.Series }
            });
        }

        /// <summary>Returns all Movie items in the library.</summary>
        public IReadOnlyList<BaseItem> GetAllMovies()
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive        = true,
                IncludeItemTypes = new[] { BaseItemKind.Movie }
            });
        }

        /// <summary>Gets the TVDB provider ID from a Jellyfin item (used to match with Sonarr).</summary>
        public string? GetTvdbId(BaseItem item)
            => item.ProviderIds.TryGetValue("Tvdb", out var id) ? id : null;

        /// <summary>Gets the TMDB provider ID from a Jellyfin item (used to match with Radarr).</summary>
        public string? GetTmdbId(BaseItem item)
            => item.ProviderIds.TryGetValue("Tmdb", out var id) ? id : null;

        // ── Internal helpers ─────────────────────────────────────────────────

        private bool IsSeriesFullyWatchedByUser(BaseItem series, object user)
        {
            var episodeQuery = new InternalItemsQuery
            {
                Parent           = series,
                Recursive        = true,
                IncludeItemTypes = new[] { BaseItemKind.Episode }
            };
            var playedQuery = new InternalItemsQuery
            {
                Parent           = series,
                Recursive        = true,
                IsPlayed         = true,
                IncludeItemTypes = new[] { BaseItemKind.Episode }
            };
            SetUserOnQuery(episodeQuery, user);
            SetUserOnQuery(playedQuery,  user);

            var total  = _libraryManager.GetItemList(episodeQuery);
            var played = _libraryManager.GetItemList(playedQuery);

            return total.Count > 0 && played.Count == total.Count;
        }

        private void AddWatched(Dictionary<Guid, List<WatchedUserInfo>> map, Guid itemId, Guid userId, string username)
        {
            if (itemId == Guid.Empty) return;
            if (!map.ContainsKey(itemId)) map[itemId] = new List<WatchedUserInfo>();
            if (!map[itemId].Any(u => u.UserId == userId))
                map[itemId].Add(new WatchedUserInfo(userId, username));
        }

        public IEnumerable<object> GetAllUsers()
        {
            var getUsersMethod = _userManager.GetType().GetMethod("GetUsers", Type.EmptyTypes);
            if (getUsersMethod != null)
            {
                var result = getUsersMethod.Invoke(_userManager, null);
                if (result is IEnumerable<object> users) return users;
                if (result is System.Collections.IEnumerable ie) return ie.Cast<object>();
            }

            var usersProp = _userManager.GetType().GetProperty("Users");
            if (usersProp != null)
            {
                var val = usersProp.GetValue(_userManager);
                if (val is IEnumerable<object> usersEnum) return usersEnum;
                if (val is System.Collections.IEnumerable ie2) return ie2.Cast<object>();
            }

            return Array.Empty<object>();
        }

        public Guid GetUserId(object user)
        {
            var idProp = user.GetType().GetProperty("Id");
            if (idProp?.GetValue(user) is Guid id) return id;
            return Guid.Empty;
        }

        public string GetUsername(object user)
        {
            var p = user.GetType().GetProperty("Username");
            if (p?.GetValue(user) is string u) return u;
            var n = user.GetType().GetProperty("Name");
            if (n?.GetValue(user) is string name) return name;
            return "Unknown";
        }

        public void SetUserOnQuery(InternalItemsQuery query, object user)
        {
            try
            {
                var prop = typeof(InternalItemsQuery).GetProperty("User");
                if (prop != null && prop.PropertyType.IsAssignableFrom(user.GetType()))
                    prop.SetValue(query, user);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set User on InternalItemsQuery.");
            }
        }
    }

    public record WatchedUserInfo(Guid UserId, string Username);
}
