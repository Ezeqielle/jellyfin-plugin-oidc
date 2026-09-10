using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OIDC.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OIDC.Services;

/// <summary>
/// Decides which Jellyfin account an authenticated external identity belongs to, creating or
/// adopting one on a first login. Kept separate from <see cref="UserSyncService"/>, which applies
/// state to an account once this has decided which account that is.
/// </summary>
public class UserResolver
{
    private readonly IUserManager _userManager;
    private readonly UserLinkStore _linkStore;
    private readonly IConfigurationStore _configurationStore;
    private readonly ILogger<UserResolver> _logger;

    public UserResolver(
        IUserManager userManager,
        UserLinkStore linkStore,
        IConfigurationStore configurationStore,
        ILogger<UserResolver> logger)
    {
        _userManager = userManager;
        _linkStore = linkStore;
        _configurationStore = configurationStore;
        _logger = logger;
    }

    /// <summary>
    /// Maps an external identity to a Jellyfin account.
    ///
    /// Resolution is by subject, never by name, once a link exists — the `sub` claim is the only
    /// identifier the IdP promises is stable and unique, so a user renamed at the IdP keeps their
    /// account and a username reassigned to someone else does not hand over the previous holder's.
    ///
    /// A first login has no link and must fall back to a name match to be useful on a server with
    /// established accounts. That fallback is the one moment an IdP account can claim a local
    /// account, so it is guarded and recorded, and it happens at most once per identity.
    /// </summary>
    public async Task<(User User, bool IsNew)> ResolveUserAsync(OidcIdentity identity)
    {
        var config = _configurationStore.Get();

        var link = _linkStore.Find(identity.Issuer, identity.Subject);
        if (link != null)
        {
            var linkedUser = _userManager.GetUserById(link.UserId);
            if (linkedUser != null)
            {
                return (linkedUser, false);
            }

            // The account was deleted in Jellyfin. Drop the dangling link and fall through, so
            // the identity is treated as new rather than failing every login from here on.
            _logger.LogWarning(
                "OIDC link for subject {Subject} points at user {UserId}, which no longer exists; relinking",
                identity.Subject, link.UserId);
            _linkStore.PruneLinks(id => _userManager.GetUserById(id) != null);
        }

        var existingByName = _userManager.GetUserByName(identity.Username);
        if (existingByName != null)
        {
            return (AdoptExistingUser(config, identity, existingByName), false);
        }

        if (!config.AutoCreateUsers)
        {
            throw new InvalidOperationException(
                $"User '{identity.Username}' does not exist and auto-creation is disabled");
        }

        var user = await _userManager.CreateUserAsync(identity.Username).ConfigureAwait(false);

        // Jellyfin requires a password on the row; this account authenticates via OIDC, so set an
        // unguessable one rather than leaving it empty.
        var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await _userManager.ChangePassword(user.Id, randomPassword).ConfigureAwait(false);

        _logger.LogInformation(
            "Created Jellyfin user {Username} for OIDC subject {Subject}",
            identity.Username, identity.Subject);

        RecordLink(identity, user.Id, adopted: false);

        return (user, true);
    }

    /// <summary>
    /// Binds an identity to a pre-existing local account of the same name, if the configuration
    /// permits it. Every refusal here is a case where proceeding would hand one person's account
    /// to another, so they fail the login rather than guessing.
    /// </summary>
    private User AdoptExistingUser(PluginConfiguration config, OidcIdentity identity, User existing)
    {
        if (config.LinkingMode != UserLinkingMode.MigrateByUsername)
        {
            throw new InvalidOperationException(
                $"A Jellyfin account named '{identity.Username}' already exists but is not linked to this "
                + "identity, and account linking by username is disabled. Link it deliberately, or rename one of the accounts.");
        }

        var conflicting = _linkStore.FindByUserId(existing.Id);
        if (conflicting != null)
        {
            throw new InvalidOperationException(
                $"Jellyfin account '{identity.Username}' is already linked to a different identity "
                + $"(subject {conflicting.Subject}). Refusing to transfer it.");
        }

        // An account carrying an SSO provider from a different plugin is not ours to claim.
        var provider = existing.AuthenticationProviderId;
        if (!string.IsNullOrEmpty(provider)
            && !provider.Contains("DefaultAuthenticationProvider", StringComparison.Ordinal)
            && !provider.Equals(typeof(Auth.OidcAuthProvider).FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Jellyfin account '{identity.Username}' is managed by another authentication provider "
                + $"({provider}). Refusing to adopt it.");
        }

        // An administrator account is the worst thing a username collision can hand over, and on
        // an established server it is among the likeliest to share a name with an IdP account.
        // There is no toggle for this: link it deliberately instead.
        if (IsAdministrator(existing))
        {
            throw new InvalidOperationException(
                $"Jellyfin account '{identity.Username}' is an administrator. Administrator accounts are "
                + "never adopted by username — add an explicit link for this identity instead.");
        }

        _logger.LogWarning(
            "Adopting existing Jellyfin account {Username} [{UserId}] for OIDC subject {Subject}. "
            + "Its watch history and preferences are preserved; its permissions are now governed by role mappings.",
            existing.Username, existing.Id, identity.Subject);

        RecordLink(identity, existing.Id, adopted: true);

        return existing;
    }

    private void RecordLink(OidcIdentity identity, Guid userId, bool adopted)
    {
        _linkStore.Link(new UserLink
        {
            ProviderId = identity.ProviderId,
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            UserId = userId,
            LinkedUsername = identity.Username,
            AdoptedExistingAccount = adopted,
            LinkedAt = DateTime.UtcNow
        });
    }

    private bool IsAdministrator(User user)
    {
        return _userManager.GetUserDto(user).Policy?.IsAdministrator ?? false;
    }
}
