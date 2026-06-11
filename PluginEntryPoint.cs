using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

#if !JELLYFIN_10_11
using MediaBrowser.Controller.Plugins;
#else
using Microsoft.Extensions.Hosting;
#endif

namespace Jellyfin.Plugin.Jellycheck
{
#if JELLYFIN_10_11
    public class PluginEntryPoint : IHostedService
#else
    public class PluginEntryPoint : IServerEntryPoint
#endif
    {
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<PluginEntryPoint> _logger;

        public PluginEntryPoint(IApplicationPaths applicationPaths, ILogger<PluginEntryPoint> logger)
        {
            _applicationPaths = applicationPaths;
            _logger = logger;
        }

        public static bool IsInjected { get; private set; }
        public static string? Error { get; private set; }
        public static string? DetectedWebPath { get; private set; }

#if JELLYFIN_10_11
        public Task StartAsync(CancellationToken cancellationToken)
        {
            RunInjection();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
#else
        public Task RunAsync()
        {
            RunInjection();
            return Task.CompletedTask;
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
                    Error = "WebPath is empty in IApplicationPaths";
                    _logger.LogWarning("Jellyfin Web path is empty. Auto-injection is disabled.");
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    Error = $"index.html not found at: {indexPath}";
                    _logger.LogWarning("Jellyfin index.html not found at: {Path}. Auto-injection is disabled.", indexPath);
                    return;
                }

                var html = File.ReadAllText(indexPath);
                var scriptTag = "<script src=\"/jellycheck/client.js\" defer></script>";

                if (!html.Contains("jellycheck/client.js"))
                {
                    _logger.LogInformation("Injecting Jellycheck client script into: {Path}", indexPath);
                    var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyIndex != -1)
                    {
                        html = html.Insert(bodyIndex, scriptTag + "\n");
                        File.WriteAllText(indexPath, html);
                        IsInjected = true;
                        _logger.LogInformation("Jellycheck client script injected successfully.");
                    }
                    else
                    {
                        Error = "Could not find closing </body> tag in index.html";
                        _logger.LogWarning("Could not find closing </body> tag in index.html. Auto-injection failed.");
                    }
                }
                else
                {
                    IsInjected = true;
                    _logger.LogInformation("Jellycheck client script is already injected in: {Path}", indexPath);
                }
            }
            catch (UnauthorizedAccessException uae)
            {
                Error = "Permission denied when writing to index.html (Read-only container or directory). See manual setup instructions.";
                _logger.LogWarning(uae, "Permission denied when trying to write to index.html. Manual setup or volume mapping required.");
            }
            catch (Exception ex)
            {
                Error = $"Exception: {ex.Message}";
                _logger.LogError(ex, "Error occurred during Jellycheck client script injection.");
            }
        }

#if !JELLYFIN_10_11
        public void Dispose()
        {
        }
#endif
    }
}
