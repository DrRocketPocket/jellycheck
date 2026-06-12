using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellycheck.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ── Auto-Delete: TV Shows ────────────────────────────────────────────
        /// <summary>Master toggle — enable automatic deletion of fully-watched TV shows.</summary>
        public bool EnableAutoDelete { get; set; } = false;

        /// <summary>Days to wait after all users have watched a show before deleting it.</summary>
        public int DeleteDelayDays { get; set; } = 14;

        // ── Auto-Delete: Movies ──────────────────────────────────────────────
        /// <summary>Separate toggle — enable automatic deletion of fully-watched movies.</summary>
        public bool EnableMovieAutoDelete { get; set; } = false;

        /// <summary>Days to wait after all users have watched a movie before deleting it.</summary>
        public int MovieDeleteDelayDays { get; set; } = 7;

        // ── Protection ───────────────────────────────────────────────────────
        /// <summary>Skip deletion if any user has the item marked as a favourite.</summary>
        public bool SkipIfFavorited { get; set; } = true;

        // ── Sonarr ───────────────────────────────────────────────────────────
        /// <summary>Sonarr base URL, e.g. http://localhost:8989</summary>
        public string SonarrUrl { get; set; } = string.Empty;

        /// <summary>Sonarr API key.</summary>
        public string SonarrApiKey { get; set; } = string.Empty;

        // ── Radarr ───────────────────────────────────────────────────────────
        /// <summary>Radarr base URL, e.g. http://localhost:7878</summary>
        public string RadarrUrl { get; set; } = string.Empty;

        /// <summary>Radarr API key.</summary>
        public string RadarrApiKey { get; set; } = string.Empty;

        // ── Overseerr ────────────────────────────────────────────────────────
        /// <summary>Overseerr base URL, e.g. http://localhost:5055</summary>
        public string OverseerrUrl { get; set; } = string.Empty;

        /// <summary>Overseerr API key.</summary>
        public string OverseerrApiKey { get; set; } = string.Empty;

        // ── Notifications ────────────────────────────────────────────────────
        /// <summary>Send a Jellyfin server notification before content is auto-deleted.</summary>
        public bool NotifyUsersBeforeDeletion { get; set; } = false;

        /// <summary>How many days before deletion to send the notification.</summary>
        public int NotifyDaysBeforeDeletion { get; set; } = 3;
    }
}
