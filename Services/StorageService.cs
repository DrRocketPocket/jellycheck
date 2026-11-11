using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
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

            // Diagnostic detail for the Check Storage panel; nothing is written
            // to the server log during normal operation (errors still log).
            void Note(string msg)
            {
                diag.Add(msg);
            }

            var asmVersion = typeof(StorageService).Assembly.GetName().Version?.ToString() ?? "?";
            Note($"Check started: plugin v{asmVersion}, refresh={forceRefresh}, plan={config.StoragePlanSizeGB} GB, " +
                 $"outsideLibrary={config.StorageUsedOutsideLibraryGB} GB, " +
                 $"sonarrUrl={(string.IsNullOrWhiteSpace(config.SonarrUrl) ? "-" : "set")}, radarrUrl={(string.IsNullOrWhiteSpace(config.RadarrUrl) ? "-" : "set")}.");

            var slotUser = DetectSlotUsername();
            if (!string.IsNullOrEmpty(slotUser))
            {
                Note($"Detected environment user: '{slotUser}'.");
            }

            // ── Tier 1: Ultra.cc / Linux System Quota (quota -s) ───────────────
            // On seedboxes like Ultra.cc, Linux user quota gives the exact account plan limit
            // (e.g. 136 GB free of 6,000 GB), whereas Radarr only sees the physical 14.5 TB shared array.
            DiskSpaceItem? quotaDisk = null;
            var (qDisk, quotaLog) = await TryGetSystemQuotaAsync(slotUser).ConfigureAwait(false);
            Note(quotaLog);
            if (qDisk != null && qDisk.TotalSpace > 0)
            {
                quotaDisk = qDisk;
                primarySource = "Ultra.cc Storage Quota";
                allDisks.Add(quotaDisk);
            }

            // ── Tier 1.5: Plan-based estimate from library media sizes ─────────
            // Works on any server: user enters their plan size (e.g. 6000 GB) and we
            // subtract the summed size of every movie/episode file in the library.
            // Less exact than quota (non-library files are not counted), but has no
            // external dependencies. Opt-in: only runs when StoragePlanSizeGB > 0.
            DiskSpaceItem? planDisk = null;
            if (quotaDisk == null && config.StoragePlanSizeGB > 0)
            {
                Note($"Plan size setting: {config.StoragePlanSizeGB} GB.");
                if (_libraryManager == null)
                {
                    Note("LibraryManager is NOT available to the storage service — cannot measure library usage.");
                }
                var usage = TryGetLibraryUsage();
                if (usage.TotalBytes > 0)
                {
                    long planTotal = (long)(config.StoragePlanSizeGB * 1024L * 1024L * 1024L);
                    long outsideUsed = (long)(config.StorageUsedOutsideLibraryGB * 1024L * 1024L * 1024L);
                    long planFree = Math.Max(0, planTotal - usage.TotalBytes - outsideUsed);
                    planDisk = new DiskSpaceItem
                    {
                        Path = "Media Library",
                        Label = "Library vs " + FormatBytes(planTotal) + " plan",
                        TotalSpace = planTotal,
                        FreeSpace = planFree
                    };
                    primarySource = "Library Estimate (Plan)";
                    allDisks.Add(planDisk);
                    Note($"Plan-based estimate: {usage.ItemCount} media items use {FormatBytes(usage.TotalBytes)} of a {FormatBytes(planTotal)} plan" +
                         (outsideUsed > 0 ? $" plus {FormatBytes(outsideUsed)} outside the library" : "") +
                         $" → {FormatBytes(planFree)} free ({usage.Method}).");
                    if (usage.IsStrmLike)
                    {
                        Note("WARNING: item sizes look like .strm placeholder files. If your media is streamed (debrid/cloud), local sizes cannot reflect it — compare with your slot's disk usage panel.");
                    }
                }
                else
                {
                    Note($"Plan size is configured but no usable media sizes were found ({usage.ItemCount} items scanned, {usage.ItemsWithSize} had recorded sizes).");
                    LogLibraryFolderState(Note);
                }
            }

            // ── Tier 2: Sonarr (Configured or Auto-discovered) ─────────────────
            bool sonarrConfigured = !string.IsNullOrWhiteSpace(config.SonarrUrl);
            bool sonarrQueried = false;

            if (sonarrConfigured)
            {
                if (string.IsNullOrWhiteSpace(config.SonarrApiKey))
                {
                    Note($"Sonarr URL is configured ({config.SonarrUrl}) but API Key is empty.");
                }
                else
                {
                    var (sonarrDisks, sonarrLog) = await _sonarr.GetDiskSpaceWithLogAsync(config.SonarrUrl, config.SonarrApiKey).ConfigureAwait(false);
                    diag.Add(sonarrLog);
                    if (sonarrDisks != null && sonarrDisks.Count > 0)
                    {
                        sonarrQueried = true;
                        if (primarySource == "None") primarySource = "Sonarr";
                        foreach (var disk in sonarrDisks) AddIfDistinct(allDisks, disk);
                    }
                }
            }

            // Try local slot auto-discovery for Sonarr if configured query didn't return disks
            if (!sonarrQueried)
            {
                var discovered = TryAutoDiscoverApp("Sonarr", slotUser);
                if (discovered != null)
                {
                    Note($"Auto-discovered local Sonarr at {discovered.Value.Url}. Testing...");
                    var (localDisks, localLog) = await _sonarr.GetDiskSpaceWithLogAsync(discovered.Value.Url, discovered.Value.ApiKey).ConfigureAwait(false);
                    Note("Local slot " + localLog);
                    if (localDisks != null && localDisks.Count > 0)
                    {
                        if (primarySource == "None") primarySource = "Sonarr (Local slot)";
                        foreach (var disk in localDisks) AddIfDistinct(allDisks, disk);
                    }
                }
            }

            // ── Tier 3: Radarr (Configured or Auto-discovered) ─────────────────
            bool radarrConfigured = !string.IsNullOrWhiteSpace(config.RadarrUrl);
            bool radarrQueried = false;

            if (radarrConfigured)
            {
                if (string.IsNullOrWhiteSpace(config.RadarrApiKey))
                {
                    Note($"Radarr URL is configured ({config.RadarrUrl}) but API Key is empty.");
                }
                else
                {
                    var (radarrDisks, radarrLog) = await _radarr.GetDiskSpaceWithLogAsync(config.RadarrUrl, config.RadarrApiKey).ConfigureAwait(false);
                    diag.Add(radarrLog);
                    if (radarrDisks != null && radarrDisks.Count > 0)
                    {
                        radarrQueried = true;
                        if (primarySource == "None") primarySource = "Radarr";
                        else if (primarySource != "Ultra.cc Storage Quota") primarySource = "Sonarr / Radarr";

                        foreach (var disk in radarrDisks) AddIfDistinct(allDisks, disk);
                    }
                }
            }

            // Try local slot auto-discovery for Radarr if configured query didn't return disks
            if (!radarrQueried)
            {
                var discovered = TryAutoDiscoverApp("Radarr", slotUser);
                if (discovered != null)
                {
                    diag.Add($"Auto-discovered local Radarr at {discovered.Value.Url}. Testing...");
                    var (localDisks, localLog) = await _radarr.GetDiskSpaceWithLogAsync(discovered.Value.Url, discovered.Value.ApiKey).ConfigureAwait(false);
                    Note("Local slot " + localLog);
                    if (localDisks != null && localDisks.Count > 0)
                    {
                        if (primarySource == "None") primarySource = "Radarr (Local slot)";
                        else if (primarySource != "Ultra.cc Storage Quota") primarySource = "Sonarr / Radarr";

                        foreach (var disk in localDisks) AddIfDistinct(allDisks, disk);
                    }
                }
            }

            // ── Tier 4: Jellyfin Media Library Directories ────────────────────
            if (allDisks.Count == 0)
            {
                try
                {
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
                                            Note($"Detected media folder '{loc}': {FormatBytes(dInfo.AvailableFreeSpace)} free of {FormatBytes(dInfo.TotalSize)}.");
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
                                        Note($"DriveInfo on '{loc}' failed: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }

                    // Fallback to DataPath
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
                                    Note($"Detected server data drive: {FormatBytes(dInfo.AvailableFreeSpace)} free of {FormatBytes(dInfo.TotalSize)}.");
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
                                Note($"DataPath DriveInfo failed: {ex.Message}");
                            }
                        }
                    }

                    // Fallback to host drives
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
                            Note($"Detected host drive: {FormatBytes(bestDrive.AvailableFreeSpace)} free of {FormatBytes(bestDrive.TotalSize)}.");
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
                    Note($"Local filesystem check error: {ex.Message}");
                }
            }

            // ── Space Calculation ─────────────────────────────────────────────
            long totalSpace = 0;
            long freeSpace = 0;

            // If Ultra.cc system quota was detected, use it as the definitive total and free space
            // so that user account limits (e.g. 136 GB free / 6000 GB) are shown instead of the physical host disk
            if (quotaDisk != null && quotaDisk.TotalSpace > 0)
            {
                totalSpace = quotaDisk.TotalSpace;
                freeSpace = quotaDisk.FreeSpace;
                primarySource = "Ultra.cc Storage Quota";
            }
            else if (planDisk != null && planDisk.TotalSpace > 0)
            {
                totalSpace = planDisk.TotalSpace;
                freeSpace = planDisk.FreeSpace;
                primarySource = "Library Estimate (Plan)";
            }
            else if (allDisks.Count > 0)
            {
                totalSpace = allDisks.Sum(d => d.TotalSpace);
                freeSpace = allDisks.Sum(d => d.FreeSpace);
            }

            if (totalSpace == 0)
            {
                if (sonarrConfigured || radarrConfigured)
                {
                    statusMessage = "Unable to reach Sonarr/Radarr. Check diagnostics.";
                }
                else
                {
                    statusMessage = "Storage details unavailable. Configure Sonarr/Radarr in settings.";
                }
            }

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

            Note($"RESULT: source='{primarySource}', total={totalSpace} bytes ({FormatBytes(totalSpace)}), free={freeSpace} bytes, message='{statusMessage}'.");

            lock (_cacheLock)
            {
                _cachedStorage = result;
                _cacheExpires = totalSpace > 0
                    ? DateTime.UtcNow.AddMinutes(2)
                    : DateTime.UtcNow.AddSeconds(5);
            }

            return result;
        }

        /// <summary>
        /// Logs every configured library and whether its folder is visible to the
        /// Jellyfin process — pinpoints permission/mount issues at a glance.
        /// </summary>
        private void LogLibraryFolderState(System.Action<string> note)
        {
            try
            {
                if (_libraryManager == null) return;
                var folders = _libraryManager.GetVirtualFolders();
                if (folders.Count == 0)
                {
                    note("No libraries are configured in Jellyfin at all.");
                    return;
                }
                foreach (var vf in folders)
                {
                    foreach (var loc in vf.Locations)
                    {
                        string state;
                        try { state = Directory.Exists(loc) ? "exists" : "NOT ACCESSIBLE"; }
                        catch (Exception ex) { state = "error: " + ex.Message; }
                        note($"Library '{vf.Name}' → '{loc}' ({state}).");
                    }
                }
            }
            catch (Exception ex)
            {
                note("Could not enumerate libraries: " + ex.Message);
            }
        }

        /// <summary>Result of the library usage scan used for the plan-based estimate.</summary>
        private sealed class LibraryUsage
        {
            public long TotalBytes;
            public int ItemCount;
            public int ItemsWithSize;
            public string Method = "none";
            public bool IsStrmLike;
        }

        /// <summary>
        /// Measures how much space the library's media occupies.
        /// Primary: sum of recorded Movie/Episode file sizes (fast, from the database).
        /// Fallback: walk the library folder locations on disk and sum real file bytes —
        /// needed when items have no recorded sizes (e.g. some .strm/debrid libraries).
        /// </summary>
        private LibraryUsage TryGetLibraryUsage()
        {
            var usage = new LibraryUsage();

            // 1. Recorded item sizes
            if (_libraryManager != null)
            {
                try
                {
                    var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        Recursive = true,
                        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode }
                    };

                    var items = _libraryManager.GetItemList(query);
                    long total = 0;
                    int count = 0;
                    int withSize = 0;
                    foreach (var item in items)
                    {
                        if (item == null) continue;
                        count++;
                        var size = item.Size;
                        if (size.HasValue && size.Value > 0)
                        {
                            total += size.Value;
                            withSize++;
                        }
                    }

                    usage.ItemCount = count;
                    usage.ItemsWithSize = withSize;
                    if (total > 0)
                    {
                        usage.TotalBytes = total;
                        usage.Method = "recorded item sizes";
                        // Every item 'has a size' but together they are implausibly tiny → .strm placeholders
                        usage.IsStrmLike = count > 0 && withSize == count && total < (long)count * 4096;
                        return usage;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Jellycheck/Storage] Failed to sum library item sizes.");
                }
            }

            // 2. Fallback: walk library folders on disk
            try
            {
                long diskTotal = 0;
                int fileCount = 0;
                if (_libraryManager != null)
                {
                    foreach (var vf in _libraryManager.GetVirtualFolders())
                    {
                        foreach (var loc in vf.Locations)
                        {
                            if (string.IsNullOrEmpty(loc) || !Directory.Exists(loc)) continue;
                            long locTotal = 0;
                            int locFiles = 0;
                            var dir = new DirectoryInfo(loc);
                            foreach (var fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                            {
                                diskTotal += fi.Length;
                                fileCount++;
                                locTotal += fi.Length;
                                locFiles++;
                            }
                        }
                    }
                }

                if (diskTotal > 0)
                {
                    usage.TotalBytes = diskTotal;
                    usage.Method = $"disk scan of library folders ({fileCount} files)";
                    usage.IsStrmLike = fileCount > 0 && diskTotal < (long)fileCount * 4096;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Jellycheck/Storage] Disk scan of library folders failed.");
            }

            return usage;
        }

        private string? DetectSlotUsername()
        {
            try
            {
                var dataPath = _applicationPaths?.DataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var match = Regex.Match(dataPath, @"/home(?:[0-9]+)?/([^/]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
                var user = Environment.GetEnvironmentVariable("USER") ?? Environment.GetEnvironmentVariable("USERNAME");
                if (!string.IsNullOrEmpty(user)) return user;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Auto-discovers local Sonarr or Radarr installation configuration on seedboxes.
        /// Checks slot directories like /home/{user}/.apps/{app}/config.xml and ~/.config/{app}/config.xml.
        /// </summary>
        private static (string Url, string ApiKey)? TryAutoDiscoverApp(string appName, string? slotUser)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME") ?? "/home";
            }

            var candidatePaths = new List<string>
            {
                Path.Combine(home, ".apps", appName.ToLowerInvariant(), "config.xml"),
                Path.Combine(home, ".config", appName, "config.xml"),
                Path.Combine(home, ".config", appName.ToLowerInvariant(), "config.xml"),
                Path.Combine("/config", "config.xml"),
                Path.Combine("/var/lib", appName.ToLowerInvariant(), "config.xml")
            };

            if (!string.IsNullOrEmpty(slotUser))
            {
                candidatePaths.Insert(0, $"/home/{slotUser}/.apps/{appName.ToLowerInvariant()}/config.xml");
                candidatePaths.Insert(1, $"/home/{slotUser}/.config/{appName}/config.xml");
                candidatePaths.Insert(2, $"/home/{slotUser}/.config/{appName.ToLowerInvariant()}/config.xml");
            }

            foreach (var path in candidatePaths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var doc = XDocument.Load(path);
                    var root = doc.Root;
                    if (root == null) continue;

                    var portStr = root.Element("Port")?.Value;
                    var urlBase = root.Element("UrlBase")?.Value ?? string.Empty;
                    var apiKey = root.Element("ApiKey")?.Value;

                    if (int.TryParse(portStr, out int port) && !string.IsNullOrWhiteSpace(apiKey))
                    {
                        var cleanUrlBase = urlBase.Trim().TrimEnd('/');
                        if (!string.IsNullOrEmpty(cleanUrlBase) && !cleanUrlBase.StartsWith("/"))
                        {
                            cleanUrlBase = "/" + cleanUrlBase;
                        }
                        var localUrl = $"http://127.0.0.1:{port}{cleanUrlBase}";
                        return (localUrl, apiKey.Trim());
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Reads Linux user quota directly via 'quota -s' (standard on Ultra.cc).
        /// </summary>
        private static async Task<(DiskSpaceItem? Disk, string Log)> TryGetSystemQuotaAsync(string? slotUser)
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix && 
                Environment.OSVersion.Platform != PlatformID.MacOSX)
            {
                return (null, "System quota skipped: not running on Linux/Unix.");
            }

            var commandsToTry = new List<(string Cmd, string Args)>
            {
                ("sh", "-c \"quota -s 2>&1\""),
                ("bash", "-c \"quota -s 2>&1\"")
            };

            if (!string.IsNullOrWhiteSpace(slotUser))
            {
                commandsToTry.Add(("sh", $"-c \"quota -s -u {slotUser} 2>&1\""));
                commandsToTry.Add(("bash", $"-c \"quota -s -u {slotUser} 2>&1\""));
                commandsToTry.Add(("quota", $"-s -u {slotUser}"));
                commandsToTry.Add(("/usr/bin/quota", $"-s -u {slotUser}"));
                commandsToTry.Add(("/bin/quota", $"-s -u {slotUser}"));
            }

            commandsToTry.Add(("quota", "-s"));
            commandsToTry.Add(("/usr/bin/quota", "-s"));
            commandsToTry.Add(("/bin/quota", "-s"));
            commandsToTry.Add(("/usr/local/bin/quota", "-s"));
            commandsToTry.Add(("sh", "-c \"app-stats show 2>&1\""));
            commandsToTry.Add(("bash", "-c \"app-stats show 2>&1\""));
            commandsToTry.Add(("app-stats", "show"));
            commandsToTry.Add(("/usr/local/bin/app-stats", "show"));
            commandsToTry.Add(("/usr/bin/app-stats", "show"));
            commandsToTry.Add(("quota", ""));
            commandsToTry.Add(("/usr/bin/quota", ""));

            foreach (var (cmd, args) in commandsToTry)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.EnvironmentVariables["LC_ALL"] = "C";
                    psi.EnvironmentVariables["LANG"] = "C";

                    using var proc = new Process { StartInfo = psi };
                    if (!proc.Start()) continue;

                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    var errorTask = proc.StandardError.ReadToEndAsync();

                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(); } catch { }
                        continue;
                    }

                    var output = await outputTask.ConfigureAwait(false);
                    var error = await errorTask.ConfigureAwait(false);
                    var combined = string.Join("\n", new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

                    // Linux 'quota' returns exit code 1 when user is near or over quota limit!
                    // Therefore, we evaluate the text output regardless of non-zero exit code.
                    if (!string.IsNullOrWhiteSpace(combined))
                    {
                        var disk = ParseQuotaOutput(combined) ?? ParseAppStatsOutput(combined);
                        if (disk != null && disk.TotalSpace > 0)
                        {
                            return (disk, $"Ultra.cc system quota detected ({disk.Path}): {FormatBytes(disk.FreeSpace)} free of {FormatBytes(disk.TotalSpace)} ('{cmd} {args}').");
                        }
                    }
                }
                catch
                {
                    // Command not found or unavailable, continue to next
                }
            }

            return (null, "No active Linux user quota detected via 'quota -s' or 'app-stats'.");
        }

        // Column names of 'quota' output — these must never be treated as filesystem candidates
        private static readonly HashSet<string> QuotaHeaderWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "filesystem", "dateisystem", "blocks", "space", "quota", "limit", "grace", "files", "user", "none"
        };

        private static bool IsFileSystemLikePath(string path)
        {
            return path.StartsWith("/", StringComparison.Ordinal) || path.Contains(":");
        }

        private static DiskSpaceItem? ParseQuotaOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(l => l.Trim())
                              .Where(l => l.Length > 0)
                              .ToList();

            // Locate the real column header row ("Filesystem  space  quota  limit ...").
            // NOTE: the preamble line ("Disk quotas for user x (uid N):") also contains the
            // word "quota" and must NOT be treated as the header — otherwise the column-name
            // row gets flattened into the data tokens and header words (e.g. "grace") are
            // misparsed as filesystem entries with bogus numbers.
            int headerIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf("Filesystem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    lines[i].IndexOf("Dateisystem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    headerIdx = i;
                    break;
                }
            }

            // Fallback for localized headers without "Filesystem": a column row contains
            // "blocks"/"space", while the preamble does not.
            if (headerIdx == -1)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].IndexOf("blocks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        lines[i].IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        headerIdx = i;
                        break;
                    }
                }
            }

            int startLine = (headerIdx >= 0 && headerIdx < lines.Count - 1) ? headerIdx + 1 : 0;

            var tokens = new List<string>();
            for (int i = startLine; i < lines.Count; i++)
            {
                var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                tokens.AddRange(parts);
            }

            if (tokens.Count < 4) return null;

            DiskSpaceItem? bestDisk = null;

            for (int i = 0; i <= tokens.Count - 4; i++)
            {
                var token = tokens[i];
                // A filesystem token typically starts with '/' or a letter, but not a digit, '*', or '-'
                if (string.IsNullOrEmpty(token) || char.IsDigit(token[0]) || token[0] == '*' || token[0] == '-')
                {
                    continue;
                }

                // Never treat quota column names as filesystems (guards against
                // header rows leaking into the token stream)
                if (QuotaHeaderWords.Contains(token))
                {
                    continue;
                }

                // If candidate is a filesystem name, the next 3 tokens are used, soft quota, hard limit
                long used = ParseQuotaBytes(tokens[i + 1]);
                long soft = ParseQuotaBytes(tokens[i + 2]);
                long hard = ParseQuotaBytes(tokens[i + 3]);
                long total = Math.Max(hard, soft);

                if (total > 0)
                {
                    long free = Math.Max(0, total - used);
                    var item = new DiskSpaceItem
                    {
                        Path = token,
                        Label = "User Quota (" + token + ")",
                        TotalSpace = total,
                        FreeSpace = free
                    };

                    bool isBetter =
                        bestDisk == null ||
                        item.TotalSpace > bestDisk.TotalSpace ||
                        // On equal totals prefer a real filesystem path (e.g. "/dev/sda1")
                        // over an arbitrary word that slipped through the filters
                        (item.TotalSpace == bestDisk.TotalSpace &&
                         !IsFileSystemLikePath(bestDisk.Path) && IsFileSystemLikePath(item.Path));

                    if (isBetter)
                    {
                        bestDisk = item;
                    }
                }
            }

            return bestDisk;
        }

        private static DiskSpaceItem? ParseAppStatsOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            var match = Regex.Match(output, @"(?:Disk|Storage)[^:\r\n]*:\s*([\d.]+)\s*([KMGTPEZY]i?B?)\s*(?:/|of)\s*([\d.]+)\s*([KMGTPEZY]i?B?)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                long used = ParseQuotaBytes(match.Groups[1].Value + match.Groups[2].Value);
                long total = ParseQuotaBytes(match.Groups[3].Value + match.Groups[4].Value);
                if (total > 0)
                {
                    long free = Math.Max(0, total - used);
                    return new DiskSpaceItem
                    {
                        Path = "Ultra.cc Slot",
                        Label = "Ultra.cc Account Storage",
                        TotalSpace = total,
                        FreeSpace = free
                    };
                }
            }
            return null;
        }

        private static long ParseQuotaBytes(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return 0;
            str = str.Replace("*", "").Trim();

            var match = Regex.Match(str, @"^([\d.]+)\s*([KMGTPEZY]i?B?)?$", RegexOptions.IgnoreCase);
            if (!match.Success) return 0;

            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
            {
                return 0;
            }

            var unit = match.Groups[2].Value.ToUpperInvariant();
            if (unit.StartsWith("P")) return (long)(num * 1024L * 1024L * 1024L * 1024L * 1024L);
            if (unit.StartsWith("T")) return (long)(num * 1024L * 1024L * 1024L * 1024L);
            if (unit.StartsWith("G")) return (long)(num * 1024L * 1024L * 1024L);
            if (unit.StartsWith("M")) return (long)(num * 1024L * 1024L);
            if (unit.StartsWith("K")) return (long)(num * 1024L);

            return (long)(num * 1024L);
        }

        private static DriveInfo? GetDriveInfoForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 1. Direct DriveInfo (works on Windows and exact Linux mount points)
            try
            {
                var direct = new DriveInfo(path);
                if (direct.TotalSize > 0) return direct;
            }
            catch { }

            // 2. Path.GetPathRoot (works on Windows: "C:\")
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

            // 3. Match against DriveInfo.GetDrives() by longest mount prefix (for Linux/Unix subdirectories)
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
