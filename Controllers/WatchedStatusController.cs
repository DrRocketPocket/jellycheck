using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;

using Microsoft.AspNetCore.Authorization;

namespace Jellyfin.Plugin.Jellycheck.Controllers
{
    [ApiController]
    [Route("jellycheck")]
    [Authorize]
    public class WatchedStatusController : ControllerBase
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<WatchedStatusController> _logger;

        public WatchedStatusController(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IApplicationPaths applicationPaths,
            ILogger<WatchedStatusController> _logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _applicationPaths = applicationPaths;
            this._logger = _logger;
        }

        [HttpGet("watched")]
        public ActionResult<Dictionary<Guid, List<UserDto>>> GetWatchedItems()
        {
            // Defense-in-depth: explicit check that user context is present and authenticated
            if (!HttpContext.Items.TryGetValue("User", out var currentUser) || currentUser == null)
            {
                return Unauthorized();
            }

            var watchedMap = new Dictionary<Guid, List<UserDto>>();
            try
            {
                var users = GetUsers();
                foreach (var userObj in users)
                {
                    if (userObj == null) continue;
                    var user = (Jellyfin.Data.Entities.User)userObj;
                    
                    var query = new InternalItemsQuery(user)
                    {
                        Recursive = true,
                        IsPlayed = true,
                        IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie }
                    };

                    var items = _libraryManager.GetItemList(query);
                    foreach (var item in items)
                    {
                        if (!watchedMap.ContainsKey(item.Id))
                        {
                            watchedMap[item.Id] = new List<UserDto>();
                        }

                        watchedMap[item.Id].Add(new UserDto
                        {
                            Id = user.Id,
                            Name = user.Username,
                            HasPrimaryImage = UserHasPrimaryImage(user)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error compiling watched items mapping in Jellycheck.");
                return StatusCode(500, "Internal server error occurred while retrieving watched status details.");
            }

            return Ok(watchedMap);
        }

        [HttpGet("client.js")]
        [AllowAnonymous]
        public IActionResult GetClientScript()
        {
            try
            {
                var assembly = typeof(WatchedStatusController).Assembly;
                using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.Jellycheck.Client.client.js");
                if (stream == null)
                {
                    return NotFound("client.js resource not found.");
                }

                using var reader = new StreamReader(stream);
                var jsContent = reader.ReadToEnd();
                return Content(jsContent, "application/javascript");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading embedded client.js resource.");
                return StatusCode(500, "Internal server error occurred while retrieving the client script.");
            }
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            if (HttpContext.Items.TryGetValue("User", out var userObj) && userObj != null)
            {
                if (!IsAdmin(userObj))
                {
                    return Forbid();
                }
            }
            else
            {
                return Unauthorized();
            }

            return Ok(new
            {
                isInjected = PluginEntryPoint.IsInjected,
                webPath = PluginEntryPoint.DetectedWebPath,
                error = PluginEntryPoint.Error
            });
        }

        private IEnumerable<object> GetUsers()
        {
            // Try GetUsers() first (Jellyfin 10.11+)
            var getUsersMethod = _userManager.GetType().GetMethod("GetUsers", Type.EmptyTypes);
            if (getUsersMethod != null)
            {
                return (IEnumerable<object>)getUsersMethod.Invoke(_userManager, null)!;
            }

            // Fallback to Users property (Jellyfin < 10.11)
            var usersProp = _userManager.GetType().GetProperty("Users");
            if (usersProp != null)
            {
                return (IEnumerable<object>)usersProp.GetValue(_userManager)!;
            }

            // Fallback to GetAllUsers()
            var getAllUsersMethod = _userManager.GetType().GetMethod("GetAllUsers", Type.EmptyTypes);
            if (getAllUsersMethod != null)
            {
                return (IEnumerable<object>)getAllUsersMethod.Invoke(_userManager, null)!;
            }

            return Array.Empty<object>();
        }

        private bool UserHasPrimaryImage(object userObj)
        {
            var user = (Jellyfin.Data.Entities.User)userObj;
            if (string.IsNullOrEmpty(user.Username)) return false;

            // Security check: validate that Username contains no path traversal sequences to prevent file checks outside the user directory
            if (user.Username.Contains('/') || user.Username.Contains('\\') || user.Username.Contains(".."))
            {
                _logger.LogWarning("Potential path traversal attempt or invalid character in Username: {Username}", user.Username);
                return false;
            }

            try
            {
                // Try checking the filesystem for the profile image
                if (!string.IsNullOrEmpty(_applicationPaths.ConfigurationDirectoryPath))
                {
                    var configUsersDir = Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "users", user.Username);
                    if (Directory.Exists(configUsersDir))
                    {
                        var files = Directory.GetFiles(configUsersDir, "profile.*");
                        if (files.Length > 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking user profile image path on disk for user {Username}", user.Username);
            }

            // Fallback reflection checks
            var hasProp = userObj.GetType().GetProperty("HasPrimaryImage");
            if (hasProp != null)
            {
                return (bool)hasProp.GetValue(userObj)!;
            }

            var pathProp = userObj.GetType().GetProperty("PrimaryImagePath");
            if (pathProp != null)
            {
                return !string.IsNullOrEmpty((string?)pathProp.GetValue(userObj));
            }

            var hasImageMethod = userObj.GetType().GetMethod("HasImage", new[] { typeof(MediaBrowser.Model.Entities.ImageType) });
            if (hasImageMethod != null)
            {
                return (bool)hasImageMethod.Invoke(userObj, new object[] { MediaBrowser.Model.Entities.ImageType.Primary })!;
            }

            return false;
        }

        private bool IsAdmin(object userObj)
        {
            try
            {
                var policyProp = userObj.GetType().GetProperty("Policy");
                if (policyProp != null)
                {
                    var policy = policyProp.GetValue(userObj);
                    if (policy != null)
                    {
                        var isAdminProp = policy.GetType().GetProperty("IsAdministrator");
                        if (isAdminProp != null)
                        {
                            return (bool)isAdminProp.GetValue(policy)!;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking admin privilege for status query.");
            }
            return false;
        }
    }

    public class UserDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool HasPrimaryImage { get; set; }
    }
}
