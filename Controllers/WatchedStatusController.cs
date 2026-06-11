using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
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
            ILogger<WatchedStatusController> logger)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _applicationPaths = applicationPaths;
            _logger = logger;
        }

        private object? GetCurrentUser()
        {
            var claimsUser = HttpContext.User;
            if (claimsUser == null)
            {
                _logger.LogWarning("GetCurrentUser failed: HttpContext.User is null.");
                return null;
            }
            if (claimsUser.Identity?.IsAuthenticated != true)
            {
                _logger.LogWarning("GetCurrentUser failed: ClaimsUser.Identity.IsAuthenticated is false.");
                return null;
            }

            var userIdClaim = claimsUser.FindFirst("Jellyfin-UserId")?.Value 
                              ?? claimsUser.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value 
                              ?? claimsUser.FindFirst("id")?.Value
                              ?? claimsUser.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim))
            {
                _logger.LogWarning("GetCurrentUser failed: NameIdentifier/id/Jellyfin-UserId claim not found.");
                return null;
            }
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                _logger.LogWarning("GetCurrentUser failed: User ID claim '{UserIdClaim}' is not a valid Guid.", userIdClaim);
                return null;
            }

            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _logger.LogWarning("GetCurrentUser failed: User not found in database for ID '{UserId}'.", userId);
                return null;
            }

            return user;
        }

        [HttpGet("watched")]
        public ActionResult<Dictionary<Guid, List<UserDto>>> GetWatchedItems()
        {
            var currentUser = GetCurrentUser();
            if (currentUser == null)
            {
                return Unauthorized();
            }

            var watchedMap = new Dictionary<Guid, List<UserDto>>();
            try
            {
                var users = GetAllUsers();
                foreach (var user in users)
                {
                    if (user == null) continue;

                    var userId = GetUserId(user);
                    var username = GetUsername(user);

                    var query = new InternalItemsQuery
                    {
                        Recursive = true,
                        IsPlayed = true,
                        IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie }
                    };

                    // Set user on query via reflection (API differs between versions)
                    SetUserOnQuery(query, user);

                    var items = _libraryManager.GetItemList(query);
                    foreach (var item in items)
                    {
                        if (!watchedMap.ContainsKey(item.Id))
                        {
                            watchedMap[item.Id] = new List<UserDto>();
                        }

                        watchedMap[item.Id].Add(new UserDto
                        {
                            Id = userId,
                            Name = username,
                            HasPrimaryImage = UserHasPrimaryImage(user, username)
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
            var currentUser = GetCurrentUser();
            if (currentUser == null)
            {
                return Unauthorized();
            }

            if (!IsAdmin(currentUser))
            {
                return Forbid();
            }

            return Ok(new
            {
                isInjected = PluginEntryPoint.IsInjected,
                webPath = PluginEntryPoint.DetectedWebPath,
                error = PluginEntryPoint.Error
            });
        }

        private IEnumerable<object> GetAllUsers()
        {
            // Try GetUsers() first (available in various versions)
            var getUsersMethod = _userManager.GetType().GetMethod("GetUsers", Type.EmptyTypes);
            if (getUsersMethod != null)
            {
                var result = getUsersMethod.Invoke(_userManager, null);
                if (result is IEnumerable<object> users) return users;
                // Handle IEnumerable<T> where T isn't object
                if (result != null)
                {
                    var enumerable = result as System.Collections.IEnumerable;
                    if (enumerable != null)
                        return enumerable.Cast<object>();
                }
            }

            // Fallback to Users property
            var usersProp = _userManager.GetType().GetProperty("Users");
            if (usersProp != null)
            {
                var val = usersProp.GetValue(_userManager);
                if (val is IEnumerable<object> usersEnum) return usersEnum;
                if (val is System.Collections.IEnumerable ie) return ie.Cast<object>();
            }

            return Array.Empty<object>();
        }

        private Guid GetUserId(object user)
        {
            var idProp = user.GetType().GetProperty("Id");
            if (idProp != null && idProp.GetValue(user) is Guid id) return id;
            return Guid.Empty;
        }

        private string GetUsername(object user)
        {
            var usernameProp = user.GetType().GetProperty("Username");
            if (usernameProp != null && usernameProp.GetValue(user) is string username) return username;
            var nameProp = user.GetType().GetProperty("Name");
            if (nameProp != null && nameProp.GetValue(user) is string name) return name;
            return "Unknown";
        }

        private void SetUserOnQuery(InternalItemsQuery query, object user)
        {
            try
            {
                // InternalItemsQuery has a User property of type Jellyfin.Data.Entities.User or similar
                var userProp = typeof(InternalItemsQuery).GetProperty("User");
                if (userProp != null && userProp.PropertyType.IsAssignableFrom(user.GetType()))
                {
                    userProp.SetValue(query, user);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set User on InternalItemsQuery, watched items may be incomplete.");
            }
        }

        private bool UserHasPrimaryImage(object userObj, string username)
        {
            if (string.IsNullOrEmpty(username)) return false;

            // Security check: validate that Username contains no path traversal sequences
            if (username.Contains('/') || username.Contains('\\') || username.Contains(".."))
            {
                _logger.LogWarning("Potential path traversal attempt or invalid character in Username: {Username}", username);
                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(_applicationPaths.ConfigurationDirectoryPath))
                {
                    var configUsersDir = Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "users", username);
                    if (Directory.Exists(configUsersDir))
                    {
                        var files = Directory.GetFiles(configUsersDir, "profile.*");
                        if (files.Length > 0) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking user profile image path for user {Username}", username);
            }

            // Fallback reflection checks
            var hasProp = userObj.GetType().GetProperty("HasPrimaryImage");
            if (hasProp != null && hasProp.GetValue(userObj) is bool hasPrimary) return hasPrimary;

            var pathProp = userObj.GetType().GetProperty("PrimaryImagePath");
            if (pathProp != null) return !string.IsNullOrEmpty(pathProp.GetValue(userObj) as string);

            var hasImageMethod = userObj.GetType().GetMethod("HasImage", new[] { typeof(MediaBrowser.Model.Entities.ImageType) });
            if (hasImageMethod != null)
                return (bool)(hasImageMethod.Invoke(userObj, new object[] { MediaBrowser.Model.Entities.ImageType.Primary }) ?? false);

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
                            return (bool)(isAdminProp.GetValue(policy) ?? false);
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
