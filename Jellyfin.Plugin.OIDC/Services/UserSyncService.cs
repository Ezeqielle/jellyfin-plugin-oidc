using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OIDC.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OIDC.Services;

/// <summary>
/// An authenticated identity as asserted by the provider's ID token.
/// </summary>
public sealed class OidcIdentity
{
    public required string ProviderId { get; init; }

    /// <summary>The `iss` claim — scopes <see cref="Subject"/> to one identity provider.</summary>
    public required string Issuer { get; init; }

    /// <summary>The `sub` claim — stable and unique within the issuer.</summary>
    public required string Subject { get; init; }

    /// <summary>
    /// From the configured username claim. Used to name a newly created account and to find an
    /// existing one on a first login; never used to identify an already-linked account.
    /// </summary>
    public required string Username { get; init; }

    public string? DisplayName { get; init; }
}

public class UserSyncService
{
    private readonly IUserManager _userManager;
    private readonly UserResolver _userResolver;
    private readonly RbacService _rbacService;
    private readonly ProfileImageService _profileImageService;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(
        IUserManager userManager,
        UserResolver userResolver,
        RbacService rbacService,
        ProfileImageService profileImageService,
        ILogger<UserSyncService> logger)
    {
        _userManager = userManager;
        _userResolver = userResolver;
        _rbacService = rbacService;
        _profileImageService = profileImageService;
        _logger = logger;
    }

    public async Task<Guid> SyncUserAsync(OidcIdentity identity, string[] roles, string? pictureUrl)
    {
        var (user, isNewUser) = await _userResolver.ResolveUserAsync(identity).ConfigureAwait(false);

        var userId = user.Id;

        if (isNewUser)
        {
            // AuthenticationProviderId is a scalar on the User row, so UpdateUserAsync persists
            // it correctly (child permissions do not persist that way — RBAC handles those via
            // UpdatePolicyAsync below). Jellyfin's UserManager uses a fresh DbContext per call
            // with an optimistic-concurrency token, and CreateUserAsync + ChangePassword advance
            // the row version, leaving the instance we hold stale — so re-fetch and retry.
            await UpdateUserResilientAsync(
                userId,
                u => u.AuthenticationProviderId = typeof(Auth.OidcAuthProvider).FullName!)
                .ConfigureAwait(false);
        }

        // RBAC applies permissions/library access, persisting via UpdatePolicyAsync (the only
        // path that saves Permission/Preference changes).
        await _rbacService.ApplyRoleMappingsAsync(userId, roles).ConfigureAwait(false);
        await _profileImageService.ApplyProfileImageAsync(userId, pictureUrl).ConfigureAwait(false);

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
