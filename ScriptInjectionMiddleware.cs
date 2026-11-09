using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycheck
{
    /// <summary>
    /// ASP.NET Core middleware that intercepts requests for Jellyfin's index.html
    /// and injects the Jellycheck client.js script tag in-memory without modifying files on disk.
    /// This works in Docker, read-only containers, and seedbox environments (e.g. Ultra.cc).
    /// </summary>
    public class ScriptInjectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ScriptInjectionMiddleware>? _logger;
        private const string ScriptTag = "<script src=\"/jellycheck/client.js\" defer onerror=\"var s=document.createElement('script');s.src='../jellycheck/client.js';s.defer=true;document.body.appendChild(s);\"></script>";

        public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware>? logger = null)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            // Only intercept the web client entry points
            bool isIndexHtml = path.Equals("/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase);

            if (!isIndexHtml)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Swap the response body with a memory stream to capture output
            var originalBodyStream = context.Response.Body;
            using var memoryStream = new MemoryStream();
            context.Response.Body = memoryStream;

            await _next(context).ConfigureAwait(false);

            // Only modify successful HTML responses
            var contentType = context.Response.ContentType ?? string.Empty;
            if (context.Response.StatusCode == 200 &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                memoryStream.Seek(0, SeekOrigin.Begin);
                var html = await new StreamReader(memoryStream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);

                if (!html.Contains("jellycheck/client.js", StringComparison.OrdinalIgnoreCase))
                {
                    var bodyIdx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyIdx != -1)
                    {
                        html = html.Insert(bodyIdx, ScriptTag + "\n");
                        _logger?.LogInformation("[Jellycheck] Script tag injected in-memory into index.html response.");
                    }
                    else
                    {
                        // Fallback: append at end
                        html += ScriptTag;
                    }
                }

                var modifiedBytes = Encoding.UTF8.GetBytes(html);
                context.Response.Body = originalBodyStream;
                context.Response.ContentLength = modifiedBytes.Length;
                await context.Response.Body.WriteAsync(modifiedBytes, 0, modifiedBytes.Length).ConfigureAwait(false);
            }
            else
            {
                // Not HTML — copy original content through unchanged
                memoryStream.Seek(0, SeekOrigin.Begin);
                context.Response.Body = originalBodyStream;
                await memoryStream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
            }
        }
    }
}
