using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OIDC.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OIDC.Services;

public class RbacService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RbacService> _logger;

    public RbacService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<RbacService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task ApplyRoleMappingsAsync(Guid userId, string[] userRoles)
    {
        var config = OidcPlugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found for RBAC application", userId);
            return;
        }

        // Read the user's current policy and mutate only the fields we manage. Persistence MUST
        // go through UpdatePolicyAsync: IUserManager.UpdateUserAsync only writes the root User row
        // and silently drops Permission/Preference changes (admin flag, enabled folders, ...),
        // which would leave users on Jellyfin's permissive default (access to all libraries).
        var policy = _userManager.GetUserDto(user).Policy;

        // OIDC-authenticated users are always enabled on login.
        policy.IsDisabled = false;

        var matchedMappings = config.RoleMappings
            .Where(m => userRoles.Contains(m.RoleName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Priority)
            .ToList();

        if (matchedMappings.Count == 0 && !string.IsNullOrEmpty(config.DefaultRoleName))
        {
            var defaultMapping = config.RoleMappings
                .FirstOrDefault(m => string.Equals(m.RoleName, config.DefaultRoleName, StringComparison.OrdinalIgnoreCase));
            if (defaultMapping != null)
            {
                matchedMappings.Add(defaultMapping);
            }
        }

        if (matchedMappings.Count == 0)
        {
            _logger.LogInformation("No role mappings matched for user {Username} with roles [{Roles}]; leaving permissions unchanged",
                user.Username, string.Join(", ", userRoles));
            await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
            return;
        }

        var merged = MergeMappings(matchedMappings);

        _logger.LogInformation(
            "RBAC matched for user {Username}: roles=[{Roles}], matched mappings=[{Matched}], resolved admin={IsAdmin}",
            user.Username,
            string.Join(", ", userRoles),
            string.Join(", ", matchedMappings.Select(m => m.RoleName)),
            merged.IsAdmin);

        policy.IsAdministrator = merged.IsAdmin;
        policy.EnableMediaPlayback = merged.EnableMediaPlayback;
        policy.EnableRemoteAccess = merged.EnableRemoteAccess;
        policy.EnableAudioPlaybackTranscoding = merged.EnableTranscoding;
        policy.EnableVideoPlaybackTranscoding = merged.EnableTranscoding;
        policy.EnableLiveTvAccess = merged.EnableLiveTv;
        policy.EnableLiveTvManagement = merged.EnableLiveTvManagement;
        policy.EnableContentDeletion = merged.EnableContentDeletion;
        policy.EnableCollectionManagement = merged.EnableCollectionManagement;
        policy.EnableSubtitleManagement = merged.EnableSubtitleManagement;

        // Administrators can access every library regardless of folder settings.
        // Force EnableAllFolders on for admins so the policy state stays consistent.
        if (merged.EnableAllLibraries || merged.IsAdmin)
        {
            policy.EnableAllFolders = true;
            policy.EnabledFolders = Array.Empty<Guid>();
        }
        else
        {
            policy.EnableAllFolders = false;
            policy.EnabledFolders = ResolveLibraryIds(merged.LibraryIds, merged.LibraryNames)
                .Select(id => Guid.TryParse(id, out var g) ? (Guid?)g : null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToArray();
        }

        if (merged.MaxParentalRating.HasValue)
        {
            policy.MaxParentalRating = merged.MaxParentalRating;
        }

        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);

        _logger.LogInformation(
            "Applied RBAC for user {Username}: admin={IsAdmin}, libraries={LibraryCount}, roles matched=[{Roles}]",
            user.Username,
            merged.IsAdmin,
            merged.EnableAllLibraries || merged.IsAdmin ? "ALL" : policy.EnabledFolders.Length.ToString(),
            string.Join(", ", matchedMappings.Select(m => m.RoleName)));
    }

    public Dictionary<string, string> GetAvailableLibraries()
    {
        var folders = _libraryManager.GetVirtualFolders();
        return folders.ToDictionary(
            f => f.ItemId,
            f => f.Name);
    }

    private List<string> ResolveLibraryIds(List<string> ids, List<string> names)
    {
        var resolved = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return resolved.ToList();
        }

        var folders = _libraryManager.GetVirtualFolders();
        foreach (var name in names)
        {
            var folder = folders.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (folder != null)
            {
                resolved.Add(folder.ItemId);
            }
            else
            {
                _logger.LogWarning("Library '{LibraryName}' not found during RBAC resolution", name);
            }
        }

        return resolved.ToList();
    }

    private static RoleMapping MergeMappings(List<RoleMapping> mappings)
    {
        var merged = new RoleMapping
        {
            IsAdmin = mappings.Any(m => m.IsAdmin),
            EnableAllLibraries = mappings.Any(m => m.EnableAllLibraries),
            EnableLiveTv = mappings.Any(m => m.EnableLiveTv),
            EnableLiveTvManagement = mappings.Any(m => m.EnableLiveTvManagement),
            EnableMediaPlayback = mappings.Any(m => m.EnableMediaPlayback),
            EnableRemoteAccess = mappings.Any(m => m.EnableRemoteAccess),
            EnableTranscoding = mappings.Any(m => m.EnableTranscoding),
            EnableContentDeletion = mappings.Any(m => m.EnableContentDeletion),
            EnableCollectionManagement = mappings.Any(m => m.EnableCollectionManagement),
            EnableSubtitleManagement = mappings.Any(m => m.EnableSubtitleManagement),
            MaxParentalRating = mappings
                .Select(m => m.MaxParentalRating)
                .Max(),
            LibraryIds = mappings
                .SelectMany(m => m.LibraryIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LibraryNames = mappings
                .SelectMany(m => m.LibraryNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        return merged;
    }
}
