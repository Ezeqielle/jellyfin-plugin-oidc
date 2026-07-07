using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OIDC.Services;

public class UserSyncService
{
    private readonly IUserManager _userManager;
    private readonly RbacService _rbacService;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(
        IUserManager userManager,
        RbacService rbacService,
        ILogger<UserSyncService> logger)
    {
        _userManager = userManager;
        _rbacService = rbacService;
        _logger = logger;
    }

    public async Task<Guid> SyncUserAsync(string username, string? displayName, string[] roles)
    {
        var user = _userManager.GetUserByName(username);
        var isNewUser = user == null;

        if (user == null)
        {
            var config = OidcPlugin.Instance?.Configuration;
            if (config?.AutoCreateUsers != true)
            {
                throw new InvalidOperationException(
                    $"User '{username}' does not exist and auto-creation is disabled");
            }

            user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);

            var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await _userManager.ChangePassword(user.Id, randomPassword).ConfigureAwait(false);

            _logger.LogInformation("Created new OIDC user: {Username}", username);
        }

        var userId = user.Id;

        if (isNewUser)
        {
            // AuthenticationProviderId is a scalar on the User row, so UpdateUserAsync persists
            // it correctly (child permissions do NOT persist that way — RBAC handles those via
            // UpdatePolicyAsync). Jellyfin's UserManager uses a fresh DbContext per call with an
            // optimistic-concurrency token, and CreateUserAsync + ChangePassword advance the row
            // version, leaving the instance we hold stale — so re-fetch a fresh copy and retry.
            await UpdateUserResilientAsync(
                userId,
                u => u.AuthenticationProviderId = typeof(Auth.OidcAuthProvider).FullName!)
                .ConfigureAwait(false);
        }

        // RBAC applies permissions/library access and re-enables the account, persisting via
        // UpdatePolicyAsync (the only path that saves Permission/Preference changes).
        await _rbacService.ApplyRoleMappingsAsync(userId, roles).ConfigureAwait(false);

        return userId;
    }

    private async Task UpdateUserResilientAsync(Guid userId, Action<User> mutate)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var user = _userManager.GetUserById(userId)
                ?? throw new InvalidOperationException($"User '{userId}' not found during sync");

            mutate(user);

            try
            {
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                && ex.GetType().Name == "DbUpdateConcurrencyException")
            {
                _logger.LogWarning(
                    "Concurrency conflict updating user {UserId} (attempt {Attempt}); retrying with a fresh copy",
                    userId, attempt);
            }
        }
    }
}
