using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OIDC.Configuration;
using Jellyfin.Plugin.OIDC.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.OIDC.Tests;

/// <summary>
/// Covers which Jellyfin account an incoming identity ends up on. The failure mode these guard
/// against is one person's account being handed to another, so the refusals matter as much as
/// the successes.
/// </summary>
public class UserResolverTests
{
    private const string Issuer = "https://auth.example.com/realms/goblin";

    private readonly Mock<IUserManager> _userManager = new(MockBehavior.Strict);
    private readonly InMemoryConfigurationStore _store = new();
    private readonly List<User> _users = new();

    private UserResolver CreateResolver()
    {
        _userManager
            .Setup(m => m.GetUserById(It.IsAny<Guid>()))
            .Returns((Guid id) => _users.FirstOrDefault(u => u.Id == id));

        _userManager
            .Setup(m => m.GetUserByName(It.IsAny<string>()))
            .Returns((string name) =>
                _users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase)));

        var linkStore = new UserLinkStore(_store, NullLogger<UserLinkStore>.Instance);
        return new UserResolver(_userManager.Object, linkStore, _store, NullLogger<UserResolver>.Instance);
    }

    private User AddUser(string username, bool isAdministrator = false, string? authProvider = null)
    {
        var user = new User(
            username,
            authProvider ?? "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");

        _users.Add(user);

        _userManager
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(new UserDto { Policy = new UserPolicy { IsAdministrator = isAdministrator } });

        return user;
    }

    private void SetupCreate(string username)
    {
        _userManager
            .Setup(m => m.CreateUserAsync(username))
            .ReturnsAsync(() => AddUser(username));

        _userManager
            .Setup(m => m.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private static OidcIdentity Identity(string username, string subject = "sub-alice")
    {
        return new OidcIdentity
        {
            ProviderId = "keycloak",
            Issuer = Issuer,
            Subject = subject,
            Username = username
        };
    }

    [Fact]
    public async Task FirstLogin_AdoptsExistingAccountAndRecordsLink()
    {
        var existing = AddUser("alice");
        var resolver = CreateResolver();

        var (user, isNew) = await resolver.ResolveUserAsync(Identity("alice"));

        Assert.Same(existing, user);
        Assert.False(isNew);

        var link = Assert.Single(_store.Configuration.UserLinks);
        Assert.Equal("sub-alice", link.Subject);
        Assert.Equal(existing.Id, link.UserId);
        Assert.True(link.AdoptedExistingAccount);
    }

    [Fact]
    public async Task RenamedAtIdp_StaysOnTheLinkedAccount()
    {
        // The reason to key on `sub`: a user renamed in Keycloak must keep their watch history,
        // not silently get a second empty account.
        var existing = AddUser("alice");
        var resolver = CreateResolver();

        await resolver.ResolveUserAsync(Identity("alice"));
        var (user, isNew) = await resolver.ResolveUserAsync(Identity("alice.newname"));

        Assert.Same(existing, user);
        Assert.False(isNew);
        Assert.Single(_store.Configuration.UserLinks);
    }

    [Fact]
    public async Task UsernameReassignedToDifferentPerson_DoesNotInheritTheAccount()
    {
        // The converse: someone taking over a freed-up username at the IdP is a different
        // subject, so they must not land on the original holder's account.
        var original = AddUser("alice");
        SetupCreate("alice");
        var resolver = CreateResolver();

        await resolver.ResolveUserAsync(Identity("alice", "sub-alice"));

        // Second identity, same username, different subject. The account is already linked.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUserAsync(Identity("alice", "sub-impostor")));

        Assert.Contains("already linked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_store.Configuration.UserLinks);
        Assert.Equal(original.Id, _store.Configuration.UserLinks[0].UserId);
    }

    [Fact]
    public async Task AdminAccount_IsNeverAdoptedByUsername()
    {
        AddUser("sam", isAdministrator: true);
        var resolver = CreateResolver();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUserAsync(Identity("sam", "sub-sam")));

        Assert.Contains("administrator", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.Configuration.UserLinks);
    }

    [Fact]
    public async Task AdminAccount_IsUsableViaAnExplicitLink()
    {
        // The supported way to put an OIDC identity on an admin account: seed the link, which
        // resolves by subject and never goes near the username fallback.
        var admin = AddUser("sam", isAdministrator: true);
        _store.Configuration.UserLinks.Add(new UserLink
        {
            ProviderId = "keycloak",
            Issuer = Issuer,
            Subject = "sub-sam",
            UserId = admin.Id,
            LinkedUsername = "sam"
        });
        var resolver = CreateResolver();

        var (user, isNew) = await resolver.ResolveUserAsync(Identity("sam", "sub-sam"));

        Assert.Same(admin, user);
        Assert.False(isNew);
    }

    [Fact]
    public async Task StrictMode_RefusesToAdoptAnUnlinkedAccount()
    {
        AddUser("alice");
        _store.Configuration.LinkingMode = UserLinkingMode.Strict;
        var resolver = CreateResolver();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUserAsync(Identity("alice")));

        Assert.Contains("not linked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.Configuration.UserLinks);
    }

    [Fact]
    public async Task StrictMode_StillHonoursAnExistingLink()
    {
        // Switching to Strict after migration must not lock out everyone already linked.
        var existing = AddUser("alice");
        var resolver = CreateResolver();
        await resolver.ResolveUserAsync(Identity("alice"));

        _store.Configuration.LinkingMode = UserLinkingMode.Strict;

        var (user, isNew) = await resolver.ResolveUserAsync(Identity("alice"));

        Assert.Same(existing, user);
        Assert.False(isNew);
    }

    [Fact]
    public async Task AccountManagedByAnotherAuthProvider_IsNotAdopted()
    {
        AddUser("bob", authProvider: "Some.Other.Sso.Provider");
        var resolver = CreateResolver();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUserAsync(Identity("bob", "sub-bob")));

        Assert.Contains("another authentication provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownUser_IsCreatedAndLinked()
    {
        SetupCreate("carol");
        var resolver = CreateResolver();

        var (user, isNew) = await resolver.ResolveUserAsync(Identity("carol", "sub-carol"));

        Assert.True(isNew);
        Assert.Equal("carol", user.Username);

        var link = Assert.Single(_store.Configuration.UserLinks);
        Assert.False(link.AdoptedExistingAccount);
        Assert.Equal(user.Id, link.UserId);
    }

    [Fact]
    public async Task UnknownUser_IsRejectedWhenAutoCreationDisabled()
    {
        _store.Configuration.AutoCreateUsers = false;
        var resolver = CreateResolver();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUserAsync(Identity("dave", "sub-dave")));

        Assert.Empty(_store.Configuration.UserLinks);
    }

    [Fact]
    public async Task DeletedAccount_IsRelinkedRatherThanFailingForever()
    {
        var original = AddUser("erin");
        var resolver = CreateResolver();
        await resolver.ResolveUserAsync(Identity("erin", "sub-erin"));

        // Admin deletes the account in Jellyfin; the link now dangles.
        _users.Remove(original);
        SetupCreate("erin");

        var (user, isNew) = await resolver.ResolveUserAsync(Identity("erin", "sub-erin"));

        Assert.True(isNew);
        Assert.NotEqual(original.Id, user.Id);

        var link = Assert.Single(_store.Configuration.UserLinks);
        Assert.Equal(user.Id, link.UserId);
    }

    [Fact]
    public async Task SubjectComparison_IsCaseSensitive()
    {
        // `sub` is opaque; two subjects differing only in case are not the same principal.
        var existing = AddUser("frank");
        SetupCreate("frank");
        var resolver = CreateResolver();

        await resolver.ResolveUserAsync(Identity("frank", "AbC"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveUserAsync(Identity("frank", "abc")));

        Assert.Contains("already linked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing.Id, _store.Configuration.UserLinks[0].UserId);
    }

    private sealed class InMemoryConfigurationStore : IConfigurationStore
    {
        public PluginConfiguration Configuration { get; private set; } = new();

        public PluginConfiguration Get() => Configuration;

        public void Save(PluginConfiguration configuration) => Configuration = configuration;
    }
}
