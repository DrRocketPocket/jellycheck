using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.Jellycheck.Configuration;

namespace Jellyfin.Plugin.Jellycheck.Services
{
    public class DiskSpaceItem
    {
        public string Path { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public long FreeSpace { get; set; }
        public long TotalSpace { get; set; }
    }

    public class DiskInfoDto
    {
        public string Path { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public long FreeSpace { get; set; }
        public long TotalSpace { get; set; }
        public double PercentUsed { get; set; }
        public string FreeSpaceFormatted { get; set; } = string.Empty;
        public string TotalSpaceFormatted { get; set; } = string.Empty;
    }

    public class StorageInfoDto
    {
        public bool Enabled { get; set; } = true;
        public long TotalSpace { get; set; }
        public long FreeSpace { get; set; }
        public long UsedSpace { get; set; }
        public double PercentUsed { get; set; }
        public string TotalSpaceFormatted { get; set; } = string.Empty;
        public string FreeSpaceFormatted { get; set; } = string.Empty;
        public string UsedSpaceFormatted { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? Message { get; set; }
        public List<DiskInfoDto> Disks { get; set; } = new();
    }

    public class StorageService
    {
        private readonly ILogger<StorageService>? _logger;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILibraryManager? _libraryManager;
        private readonly SonarrService _sonarr;
        private readonly RadarrService _radarr;

        private static StorageInfoDto? _cachedStorage;
        private static DateTime _cacheExpires = DateTime.MinValue;
        private static readonly object _cacheLock = new object();

        public StorageService(
            ILogger<StorageService>? logger,
            IApplicationPaths applicationPaths,
            ILibraryManager? libraryManager = null,
            SonarrService? sonarr = null,
            RadarrService? radarr = null)
        {
            _logger = logger;
            _applicationPaths = applicationPaths;
            _libraryManager = libraryManager;
            _sonarr = sonarr ?? new SonarrService(null);
            _radarr = radarr ?? new RadarrService(null);
        }

        public async Task<StorageInfoDto> GetStorageAsync(bool forceRefresh = false)
        {
            if (!forceRefresh)
            {
                lock (_cacheLock)
                {
                    if (_cachedStorage != null && DateTime.UtcNow < _cacheExpires)
                    {
                        return _cachedStorage;
                    }
                }
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var allDisks = new List<DiskSpaceItem>();
            string primarySource = "None";
            string statusMessage = string.Empty;

            // 1. Try Sonarr
            bool sonarrConfigured = !string.IsNullOrWhiteSpace(config.SonarrUrl) && !string.IsNullOrWhiteSpace(config.SonarrApiKey);
            if (sonarrConfigured)
            {
                var sonarrDisks = await _sonarr.GetDiskSpaceAsync(config.SonarrUrl, config.SonarrApiKey).ConfigureAwait(false);
                if (sonarrDisks != null && sonarrDisks.Count > 0)
                {
                    primarySource = "Sonarr";
                    foreach (var disk in sonarrDisks)
                    {
                        AddIfDistinct(allDisks, disk);
                    }
                }
            }

            // 2. Try Radarr
            bool radarrConfigured = !string.IsNullOrWhiteSpace(config.RadarrUrl) && !string.IsNullOrWhiteSpace(config.RadarrApiKey);
            if (radarrConfigured)
            {
                var radarrDisks = await _radarr.GetDiskSpaceAsync(config.RadarrUrl, config.RadarrApiKey).ConfigureAwait(false);
                if (radarrDisks != null && radarrDisks.Count > 0)
                {
                    if (primarySource == "None") primarySource = "Radarr";
                    else primarySource = "Sonarr / Radarr";

                    foreach (var disk in radarrDisks)
                    {
                        AddIfDistinct(allDisks, disk);
                    }
                }
            }

            // 3. Fallback: Host and Library DriveInfo
            if (allDisks.Count == 0)
            {
                try
                {
                    // Inspect Jellyfin's configured media library directories
                    if (_libraryManager != null)
                    {
                        var virtualFolders = _libraryManager.GetVirtualFolders();
                        foreach (var vf in virtualFolders)
                        {
                            foreach (var loc in vf.Locations)
                            {
                                if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                                {
                                    try
                                    {
                                        var dInfo = new DriveInfo(loc);
                                        if (dInfo.TotalSize > 0)
                                        {
                                            primarySource = "Jellyfin Media Drive";
                                            AddIfDistinct(allDisks, new DiskSpaceItem
                                            {
                                                Path = loc,
                                                Label = vf.Name ?? dInfo.Name,
                                                FreeSpace = dInfo.AvailableFreeSpace,
                                                TotalSpace = dInfo.TotalSize
                                            });
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    // Fallback to data path / root drive
                    if (allDisks.Count == 0)
                    {
                        var dataPath = _applicationPaths?.DataPath;
                        if (!string.IsNullOrEmpty(dataPath) && Directory.Exists(dataPath))
                        {
                            try
                            {
                                var dInfo = new DriveInfo(dataPath);
                                if (dInfo.TotalSize > 0)
                                {
                                    primarySource = "Server Storage";
                                    allDisks.Add(new DiskSpaceItem
                                    {
                                        Path = dInfo.Name,
                                        Label = string.IsNullOrWhiteSpace(dInfo.VolumeLabel) ? dInfo.Name : dInfo.VolumeLabel,
                                        FreeSpace = dInfo.AvailableFreeSpace,
                                        TotalSpace = dInfo.TotalSize
                                    });
                                }
                            }
                            catch { }
                        }
                    }

                    // Fallback to DriveInfo.GetDrives()
                    if (allDisks.Count == 0)
                    {
                        var hostDrives = DriveInfo.GetDrives()
                            .Where(d => {
                                try { return d.IsReady && d.TotalSize > 0; }
                                catch { return false; }
                            })
                            .ToList();

                        if (hostDrives.Count > 0)
                        {
                            var bestDrive = hostDrives.OrderByDescending(d => d.TotalSize).First();
                            primarySource = "Host Storage";
                            allDisks.Add(new DiskSpaceItem
                            {
                                Path = bestDrive.Name,
                                Label = string.IsNullOrWhiteSpace(bestDrive.VolumeLabel) ? bestDrive.Name : bestDrive.VolumeLabel,
                                FreeSpace = bestDrive.AvailableFreeSpace,
                                TotalSpace = bestDrive.TotalSize
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Jellycheck/Storage] Failed to read local drive information.");
                }
            }

            if (allDisks.Count == 0)
            {
                if (sonarrConfigured || radarrConfigured)
                {
                    statusMessage = "Could not reach Sonarr/Radarr. Check URL (include https://) and API key.";
                }
                else
                {
                    statusMessage = "Sonarr/Radarr not configured in Jellycheck settings.";
                }
            }

            long totalSpace = allDisks.Sum(d => d.TotalSpace);
            long freeSpace = allDisks.Sum(d => d.FreeSpace);
            long usedSpace = Math.Max(0, totalSpace - freeSpace);
            double pctUsed = totalSpace > 0 ? Math.Round((double)usedSpace / totalSpace * 100.0, 1) : 0.0;

            var result = new StorageInfoDto
            {
                Enabled = config.ShowStorageOnHomePage,
                TotalSpace = totalSpace,
                FreeSpace = freeSpace,
                UsedSpace = usedSpace,
                PercentUsed = pctUsed,
                TotalSpaceFormatted = FormatBytes(totalSpace),
                FreeSpaceFormatted = FormatBytes(freeSpace),
                UsedSpaceFormatted = FormatBytes(usedSpace),
                Source = primarySource,
                Message = statusMessage,
                Disks = allDisks.Select(d =>
                {
                    long diskUsed = Math.Max(0, d.TotalSpace - d.FreeSpace);
                    double diskPct = d.TotalSpace > 0 ? Math.Round((double)diskUsed / d.TotalSpace * 100.0, 1) : 0.0;
                    return new DiskInfoDto
                    {
                        Path = d.Path,
                        Label = d.Label,
                        TotalSpace = d.TotalSpace,
                        FreeSpace = d.FreeSpace,
                        PercentUsed = diskPct,
                        TotalSpaceFormatted = FormatBytes(d.TotalSpace),
                        FreeSpaceFormatted = FormatBytes(d.FreeSpace)
                    };
                }).ToList()
            };

            lock (_cacheLock)
            {
                _cachedStorage = result;
                _cacheExpires = DateTime.UtcNow.AddMinutes(2);
            }

            return result;
        }

        private static void AddIfDistinct(List<DiskSpaceItem> list, DiskSpaceItem item)
        {
            if (item.TotalSpace <= 0) return;

            if (list.Any(d => string.Equals(d.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // On seedbox services like Ultra.cc, tv and movie root folders reside in the same user quota.
            // If totalSpace is identical and freeSpace is within 2 GB of each other, consider it the same disk/quota.
            const long tolerance = 2L * 1024L * 1024L * 1024L; // 2 GB
            if (list.Any(d => d.TotalSpace == item.TotalSpace && Math.Abs(d.FreeSpace - item.FreeSpace) <= tolerance))
            {
                return;
            }

            list.Add(item);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024m) >= 1 && counter < suffixes.Length - 1)
            {
                number /= 1024m;
                counter++;
            }
            return $"{number:F1} {suffixes[counter]}";
        }
    }
}
