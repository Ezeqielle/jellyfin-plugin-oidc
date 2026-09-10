using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.OIDC.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OIDC.Services;

/// <summary>
/// Stores the bindings between external identities (issuer + subject) and Jellyfin accounts.
///
/// Links live in the plugin configuration, which Jellyfin persists as XML. Logins can overlap,
/// and the configuration object is read-modify-write, so every mutation here is serialised
/// behind a lock — two first-time logins arriving together must not lose one another's link.
/// </summary>
public sealed class UserLinkStore
{
    private readonly object _writeLock = new();
    private readonly IConfigurationStore _configurationStore;
    private readonly ILogger<UserLinkStore> _logger;

    public UserLinkStore(IConfigurationStore configurationStore, ILogger<UserLinkStore> logger)
    {
        _configurationStore = configurationStore;
        _logger = logger;
    }

    /// <summary>
    /// Returns the account linked to this identity, or null when the identity is unlinked.
    /// </summary>
    public UserLink? Find(string issuer, string subject)
    {
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
        {
            return null;
        }

        return _configurationStore.Get().UserLinks
            .FirstOrDefault(l => Matches(l, issuer, subject));
    }

    /// <summary>
    /// Returns the link held on a Jellyfin account, or null when the account is unlinked.
    /// Used to refuse adopting an account that already belongs to a different identity.
    /// </summary>
    public UserLink? FindByUserId(Guid userId)
    {
        return _configurationStore.Get().UserLinks.FirstOrDefault(l => l.UserId == userId);
    }

    /// <summary>
    /// Records a link, replacing any existing link for the same identity. Idempotent: re-linking
    /// an identity to the account it already points at rewrites nothing and skips the save.
    /// </summary>
    public void Link(UserLink link)
    {
        lock (_writeLock)
        {
            var config = _configurationStore.Get();
            var existing = config.UserLinks.FirstOrDefault(l => Matches(l, link.Issuer, link.Subject));

            if (existing != null)
            {
                if (existing.UserId == link.UserId)
                {
                    return;
                }

                _logger.LogWarning(
                    "Re-pointing OIDC link for subject {Subject} from user {OldUserId} to {NewUserId}",
                    link.Subject, existing.UserId, link.UserId);

                config.UserLinks.Remove(existing);
            }

            config.UserLinks.Add(link);
            _configurationStore.Save(config);

            _logger.LogInformation(
                "Linked OIDC identity {Subject} ({Issuer}) to Jellyfin user {Username} [{UserId}]{Adopted}",
                link.Subject, link.Issuer, link.LinkedUsername, link.UserId,
                link.AdoptedExistingAccount ? " by adopting an existing account" : string.Empty);
        }
    }

    /// <summary>
    /// Drops links pointing at accounts that no longer exist, so a deleted-and-recreated user
    /// does not resolve to a dangling GUID forever. Returns the number of links removed.
    /// </summary>
    public int PruneLinks(Func<Guid, bool> userExists)
    {
        lock (_writeLock)
        {
            var config = _configurationStore.Get();
            var stale = config.UserLinks.Where(l => !userExists(l.UserId)).ToList();
            if (stale.Count == 0)
            {
                return 0;
            }

            foreach (var link in stale)
            {
                _logger.LogInformation(
                    "Removing stale OIDC link for subject {Subject}: user {UserId} no longer exists",
                    link.Subject, link.UserId);
                config.UserLinks.Remove(link);
            }

            _configurationStore.Save(config);
            return stale.Count;
        }
    }

    private static bool Matches(UserLink link, string issuer, string subject)
    {
        // Subject comparison is ordinal and case-sensitive: `sub` is an opaque identifier, and
        // nothing licenses us to treat two subjects differing in case as the same principal.
        return string.Equals(link.Subject, subject, StringComparison.Ordinal)
               && string.Equals(link.Issuer, issuer, StringComparison.OrdinalIgnoreCase);
    }
}
