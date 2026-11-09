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
        public List<string> Diagnostics { get; set; } = new();
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
            var diag = new List<string>();
            string primarySource = "None";
            string statusMessage = string.Empty;

            // 1. Try Sonarr
            bool sonarrConfigured = !string.IsNullOrWhiteSpace(config.SonarrUrl);
            if (sonarrConfigured)
            {
                if (string.IsNullOrWhiteSpace(config.SonarrApiKey))
                {
                    diag.Add($"Sonarr URL is set ({config.SonarrUrl}) but API Key is empty.");
                }
                else
                {
                    var (sonarrDisks, sonarrLog) = await _sonarr.GetDiskSpaceWithLogAsync(config.SonarrUrl, config.SonarrApiKey).ConfigureAwait(false);
                    diag.Add(sonarrLog);
                    if (sonarrDisks != null && sonarrDisks.Count > 0)
                    {
                        primarySource = "Sonarr";
                        foreach (var disk in sonarrDisks)
                        {
                            AddIfDistinct(allDisks, disk);
                        }
                    }
                }
            }
            else
            {
                diag.Add("Sonarr is not configured.");
            }

            // 2. Try Radarr
            bool radarrConfigured = !string.IsNullOrWhiteSpace(config.RadarrUrl);
            if (radarrConfigured)
            {
                if (string.IsNullOrWhiteSpace(config.RadarrApiKey))
                {
                    diag.Add($"Radarr URL is set ({config.RadarrUrl}) but API Key is empty.");
                }
                else
                {
                    var (radarrDisks, radarrLog) = await _radarr.GetDiskSpaceWithLogAsync(config.RadarrUrl, config.RadarrApiKey).ConfigureAwait(false);
                    diag.Add(radarrLog);
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
            }
            else
            {
                diag.Add("Radarr is not configured.");
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
                                        var dInfo = GetDriveInfoForPath(loc);
                                        if (dInfo != null && dInfo.TotalSize > 0)
                                        {
                                            diag.Add($"Detected media folder '{loc}': {FormatBytes(dInfo.AvailableFreeSpace)} free of {FormatBytes(dInfo.TotalSize)}.");
                                            primarySource = "Jellyfin Media Library";
                                            AddIfDistinct(allDisks, new DiskSpaceItem
                                            {
                                                Path = loc,
                                                Label = vf.Name ?? dInfo.Name,
                                                FreeSpace = dInfo.AvailableFreeSpace,
                                                TotalSpace = dInfo.TotalSize
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        diag.Add($"DriveInfo on '{loc}' failed: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }

                    // Fallback to data path
                    if (allDisks.Count == 0)
                    {
                        var dataPath = _applicationPaths?.DataPath;
                        if (!string.IsNullOrEmpty(dataPath) && Directory.Exists(dataPath))
                        {
                            try
                            {
                                var dInfo = GetDriveInfoForPath(dataPath);
                                if (dInfo != null && dInfo.TotalSize > 0)
                                {
                                    diag.Add($"Detected server data drive: {FormatBytes(dInfo.AvailableFreeSpace)} free of {FormatBytes(dInfo.TotalSize)}.");
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
                            catch (Exception ex)
                            {
                                diag.Add($"DataPath DriveInfo failed: {ex.Message}");
                            }
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
                            diag.Add($"Detected host drive: {FormatBytes(bestDrive.AvailableFreeSpace)} free of {FormatBytes(bestDrive.TotalSize)}.");
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
                    diag.Add($"Local filesystem check error: {ex.Message}");
                }
            }

            if (allDisks.Count == 0)
            {
                if (sonarrConfigured || radarrConfigured)
                {
                    statusMessage = "Unable to connect to Sonarr/Radarr. Check URL and API key.";
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
                Diagnostics = diag,
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
                // Only cache successful results for 2 minutes; retry failures quickly (5 seconds)
                _cacheExpires = totalSpace > 0
                    ? DateTime.UtcNow.AddMinutes(2)
                    : DateTime.UtcNow.AddSeconds(5);
            }

            return result;
        }

        private static DriveInfo? GetDriveInfoForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 1. Try direct DriveInfo (works on Windows and exact Linux mount points)
            try
            {
                var direct = new DriveInfo(path);
                if (direct.TotalSize > 0) return direct;
            }
            catch { }

            // 2. Try Path.GetPathRoot (works on Windows: "C:\")
            try
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var directRoot = new DriveInfo(root);
                    if (directRoot.TotalSize > 0) return directRoot;
                }
            }
            catch { }

            // 3. Match against DriveInfo.GetDrives() by longest mount prefix (essential for Linux/Unix subdirectories)
            try
            {
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                DriveInfo? bestMatch = null;
                int bestLength = -1;

                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady || drive.TotalSize <= 0) continue;
                        var mount = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (string.Equals(fullPath, mount, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(mount + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            (string.IsNullOrEmpty(mount) && fullPath.StartsWith("/")))
                        {
                            if (mount.Length > bestLength)
                            {
                                bestLength = mount.Length;
                                bestMatch = drive;
                            }
                        }
                    }
                    catch { }
                }

                if (bestMatch != null) return bestMatch;
            }
            catch { }

            return null;
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
