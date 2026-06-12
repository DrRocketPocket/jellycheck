using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck.Services
{
    /// <summary>
    /// Persists "fully-watched since" timestamps for series and movies to a JSON file
    /// in the Jellyfin plugin data directory. This file survives server restarts.
    ///
    /// Entry lifecycle:
    ///   - Added when all users have watched all available episodes/a movie.
    ///   - Removed when a new un-watched episode appears (clock resets) OR when the item is deleted.
    ///   - The scheduled task checks entries ≥ N days old and deletes those items.
    /// </summary>
    public class WatchedStateStore
    {
        private readonly string _filePath;
        private readonly ILogger<WatchedStateStore> _logger;
        private readonly object _lock = new();

        public WatchedStateStore(IApplicationPaths appPaths, ILogger<WatchedStateStore> logger)
        {
            _logger   = logger;
            _filePath = Path.Combine(appPaths.PluginConfigurationsPath, "jellycheck_watched_state.json");
        }

        // ── Read ─────────────────────────────────────────────────────────────

        public Dictionary<Guid, WatchedStateEntry> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                    return new Dictionary<Guid, WatchedStateEntry>();

                try
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<Dictionary<Guid, WatchedStateEntry>>(json)
                           ?? new Dictionary<Guid, WatchedStateEntry>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load Jellycheck watched state from {Path}", _filePath);
                    return new Dictionary<Guid, WatchedStateEntry>();
                }
            }
        }

        // ── Write ────────────────────────────────────────────────────────────

        public void Save(Dictionary<Guid, WatchedStateEntry> state)
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save Jellycheck watched state to {Path}", _filePath);
                }
            }
        }

        // ── Convenience helpers ───────────────────────────────────────────────

        /// <summary>Records the current time as the "fully watched since" timestamp for itemId.</summary>
        public void SetFullyWatchedNow(Guid itemId, string itemName, WatchedItemKind kind)
        {
            var state = Load();
            state[itemId] = new WatchedStateEntry
            {
                ItemId           = itemId,
                ItemName         = itemName,
                Kind             = kind,
                FullyWatchedSince = DateTimeOffset.UtcNow
            };
            Save(state);
            _logger.LogInformation("[Jellycheck] Recorded fully-watched timestamp for {Name} ({Kind}) at {Time}",
                itemName, kind, DateTimeOffset.UtcNow);
        }

        /// <summary>Removes the tracking entry for itemId (new episode appeared, or item was deleted).</summary>
        public void Remove(Guid itemId)
        {
            var state = Load();
            if (state.Remove(itemId))
                Save(state);
        }

        /// <summary>Returns how many days have elapsed since the fully-watched timestamp, or null if not set.</summary>
        public double? DaysSinceFullyWatched(Guid itemId)
        {
            var state = Load();
            if (!state.TryGetValue(itemId, out var entry)) return null;
            return (DateTimeOffset.UtcNow - entry.FullyWatchedSince).TotalDays;
        }
    }

    public class WatchedStateEntry
    {
        public Guid ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public WatchedItemKind Kind { get; set; }
        public DateTimeOffset FullyWatchedSince { get; set; }
    }

    public enum WatchedItemKind { Series, Movie }
}
