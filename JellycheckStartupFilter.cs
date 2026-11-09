using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Jellycheck
{
    /// <summary>
    /// Registers the ScriptInjectionMiddleware into the ASP.NET Core pipeline via IStartupFilter.
    /// This is required because Jellyfin plugins cannot directly access IApplicationBuilder,
    /// but IStartupFilter runs during startup and lets us insert middleware.
    /// </summary>
    public class JellycheckStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseMiddleware<ScriptInjectionMiddleware>();
                next(builder);
            };
        }
    }
}
