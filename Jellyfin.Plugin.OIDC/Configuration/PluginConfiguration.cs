using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OIDC.Configuration;

/// <summary>
/// How an incoming OIDC identity is matched to a Jellyfin account on its first login, before
/// any link exists. Once linked, the identity is resolved by subject and this no longer applies.
/// </summary>
public enum UserLinkingMode
{
    /// <summary>
    /// Adopt an existing Jellyfin account whose username matches the incoming username claim.
    /// This is the migration path for a server with established local accounts: the account and
    /// all its watch state are preserved, and the link is recorded so the match happens once.
    /// </summary>
    MigrateByUsername = 0,

    /// <summary>
    /// Never adopt an unlinked account. An identity with no link is a new user, subject to
    /// <see cref="PluginConfiguration.AutoCreateUsers"/>. Use once migration is complete: it
    /// closes the window in which an IdP account can claim a local account by name.
    /// </summary>
    Strict = 1
}

public class PluginConfiguration : BasePluginConfiguration
{
    public List<OidcProviderConfig> Providers { get; set; } = new();

    public List<RoleMapping> RoleMappings { get; set; } = new();

    public string DefaultProvider { get; set; } = string.Empty;

    public bool AutoCreateUsers { get; set; } = true;

    public string DefaultRoleName { get; set; } = string.Empty;

    /// <summary>
    /// How unlinked identities are matched to existing accounts. See <see cref="UserLinkingMode"/>.
    /// </summary>
    public UserLinkingMode LinkingMode { get; set; } = UserLinkingMode.MigrateByUsername;

    /// <summary>
    /// Identity-to-account links, keyed on the IdP's issuer and subject. The subject is the only
    /// identifier an IdP promises is stable and unique — usernames and email addresses can both
    /// be changed and reassigned — so once a link exists it takes precedence over any name match.
    /// </summary>
    public List<UserLink> UserLinks { get; set; } = new();
}

/// <summary>
/// A resolved binding between an external identity and a Jellyfin account.
/// </summary>
public class UserLink
{
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Issuer that asserted the subject; scopes the subject to one IdP.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The `sub` claim — stable and unique within the issuer.</summary>
    public string Subject { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>
    /// Username at the time of linking. Informational only — it is not used for matching, and
    /// goes stale if the account is renamed on either side. Kept so the admin UI and logs can
    /// show a human-readable link.
    /// </summary>
    public string LinkedUsername { get; set; } = string.Empty;

    /// <summary>Whether the link was made by adopting a pre-existing account, or by creating one.</summary>
    public bool AdoptedExistingAccount { get; set; }

    public DateTime LinkedAt { get; set; }
}

public class OidcProviderConfig
{
    public string ProviderId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string Scopes { get; set; } = "openid profile email";

    public string RoleClaim { get; set; } = "realm_access.roles";

    public string UsernameClaim { get; set; } = "preferred_username";

    public string DisplayNameClaim { get; set; } = "name";

    public string PictureClaim { get; set; } = "picture";

    public bool SyncProfileImage { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public string ButtonColor { get; set; } = "#4285F4";

    public string ButtonIcon { get; set; } = string.Empty;

    public string AdditionalParameters { get; set; } = string.Empty;

    public string ServerBaseUrl { get; set; } = string.Empty;
}

public class RoleMapping
{
    public string RoleName { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool EnableAllLibraries { get; set; }

    public List<string> LibraryIds { get; set; } = new();

    public List<string> LibraryNames { get; set; } = new();

    public bool EnableLiveTv { get; set; }

    public bool EnableLiveTvManagement { get; set; }

    public bool EnableMediaPlayback { get; set; } = true;

    public bool EnableRemoteAccess { get; set; } = true;

    public bool EnableTranscoding { get; set; } = true;

    public bool EnableContentDeletion { get; set; }

    public bool EnableCollectionManagement { get; set; }

    public bool EnableSubtitleManagement { get; set; }

    public int? MaxParentalRating { get; set; }

    public int Priority { get; set; }
}
