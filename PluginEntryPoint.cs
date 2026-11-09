using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#if !JELLYFIN_10_11
using MediaBrowser.Controller.Plugins;
#endif

namespace Jellyfin.Plugin.Jellycheck
{
    public class PluginEntryPoint : IHostedService
#if !JELLYFIN_10_11
        , IServerEntryPoint
#endif
    {
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<PluginEntryPoint> _logger;

        public PluginEntryPoint(IApplicationPaths applicationPaths, ILogger<PluginEntryPoint> logger)
        {
            _applicationPaths = applicationPaths;
            _logger = logger;
        }

        public static bool IsInjected { get; internal set; }
        public static string? Error { get; private set; }
        public static string? DetectedWebPath { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            RunInjection();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

#if !JELLYFIN_10_11
        public Task RunAsync()
        {
            RunInjection();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
#endif

        private void RunInjection()
        {
            _logger.LogInformation("Jellycheck plugin entry point running.");
            try
            {
                var webPath = _applicationPaths.WebPath;
                DetectedWebPath = webPath;

                if (string.IsNullOrEmpty(webPath))
                {
                    _logger.LogInformation("Jellyfin WebPath is empty. Will rely on in-memory middleware injection.");
                    IsInjected = true;
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogInformation("Jellyfin index.html not found at: {Path}. Will rely on in-memory middleware injection.", indexPath);
                    IsInjected = true;
                    return;
                }

                // Try disk-based injection (backwards compatible for writable environments)
                var html = File.ReadAllText(indexPath);
                var oldScriptTag = "<script src=\"/jellycheck/client.js\" defer></script>";
                var newScriptTag = "<script src=\"../jellycheck/client.js\" defer></script>";

                bool modified = false;
                if (html.Contains(oldScriptTag))
                {
                    _logger.LogInformation("Updating Jellycheck script tag path in index.html");
                    html = html.Replace(oldScriptTag, newScriptTag);
                    modified = true;
                }

                if (!html.Contains("jellycheck/client.js"))
                {
                    _logger.LogInformation("Injecting Jellycheck client script into: {Path}", indexPath);
                    var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyIndex != -1)
                    {
                        html = html.Insert(bodyIndex, newScriptTag + "\n");
                        modified = true;
                    }
                    else
                    {
                        _logger.LogWarning("Could not find closing </body> tag in index.html. Will rely on in-memory middleware injection.");
                    }
                }

                if (modified)
                {
                    File.WriteAllText(indexPath, html);
                    _logger.LogInformation("Jellycheck client script injected/updated on disk successfully.");
                }

                IsInjected = html.Contains("jellycheck/client.js");
            }
            catch (UnauthorizedAccessException uae)
            {
                _logger.LogInformation("Cannot write to index.html ({Message}). In-memory middleware injection active.", uae.Message);
                IsInjected = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disk-based script injection failed. In-memory middleware injection active.");
                IsInjected = true;
            }
        }
    }
}
