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
/// Covers deprovisioning: what happens to an account whose IdP roles stop justifying the
/// permissions it holds.
/// </summary>
public class RbacServiceTests
{
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly InMemoryConfigurationStore _store = new();

    private UserPolicy? _savedPolicy;

    private RbacService CreateService(User user, UserPolicy currentPolicy)
    {
        _userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        _userManager.Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(new UserDto { Policy = currentPolicy });
        _userManager.Setup(m => m.UpdatePolicyAsync(user.Id, It.IsAny<UserPolicy>()))
            .Callback((Guid _, UserPolicy p) => _savedPolicy = p)
            .Returns(Task.CompletedTask);

        return new RbacService(
            _userManager.Object,
            _libraryManager.Object,
            _store,
            NullLogger<RbacService>.Instance);
    }

    private static User NewUser(string name = "chowder") => new(
        name,
        "Jellyfin.Plugin.OIDC.Auth.OidcAuthProvider",
        "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");

    [Fact]
    public async Task NoMatchingRole_RevokesAdministrator()
    {
        // The bug this replaced: the method returned early, so an admin who lost their role at
        // the IdP stayed an admin in Jellyfin forever.
        var user = NewUser();
        var service = CreateService(user, new UserPolicy { IsAdministrator = true });

        await service.ApplyRoleMappingsAsync(user.Id, new[] { "some-unmapped-role" });

        Assert.NotNull(_savedPolicy);
        Assert.False(_savedPolicy!.IsAdministrator);
    }

    [Fact]
    public async Task NoMatchingRole_DoesNotLockTheUserOut()
    {
        // Only admin is revoked. A mapping typo should not disable people's media access.
        var user = NewUser();
        var service = CreateService(
            user,
            new UserPolicy { IsAdministrator = true, IsDisabled = false, EnableMediaPlayback = true });

        await service.ApplyRoleMappingsAsync(user.Id, new[] { "some-unmapped-role" });

        Assert.False(_savedPolicy!.IsDisabled);
        Assert.True(_savedPolicy.EnableMediaPlayback);
    }

    [Fact]
    public async Task MatchingAdminRole_GrantsAdministratorAndAllLibraries()
    {
        var user = NewUser();
        _store.Configuration.RoleMappings.Add(new RoleMapping
        {
            RoleName = "jellyfin-admin",
            IsAdmin = true
        });

        var service = CreateService(user, new UserPolicy { IsAdministrator = false });

        await service.ApplyRoleMappingsAsync(user.Id, new[] { "jellyfin-admin" });

        Assert.True(_savedPolicy!.IsAdministrator);
        Assert.True(_savedPolicy.EnableAllFolders);
    }

    [Fact]
    public async Task MatchingRole_ReEnablesAPreviouslyDisabledAccount()
    {
        var user = NewUser();
        _store.Configuration.RoleMappings.Add(new RoleMapping { RoleName = "friend" });

        var service = CreateService(user, new UserPolicy { IsDisabled = true });

        await service.ApplyRoleMappingsAsync(user.Id, new[] { "friend" });

        Assert.False(_savedPolicy!.IsDisabled);
    }

    [Fact]
    public async Task RoleMatchingIsCaseInsensitive()
    {
        // IdPs differ on the casing of group names; a case difference should not silently
        // demote someone.
        var user = NewUser();
        _store.Configuration.RoleMappings.Add(new RoleMapping
        {
            RoleName = "Jellyfin-Admin",
            IsAdmin = true
        });

        var service = CreateService(user, new UserPolicy());

        await service.ApplyRoleMappingsAsync(user.Id, new[] { "jellyfin-admin" });

        Assert.True(_savedPolicy!.IsAdministrator);
    }

    private sealed class InMemoryConfigurationStore : IConfigurationStore
    {
        public PluginConfiguration Configuration { get; private set; } = new();

        public PluginConfiguration Get() => Configuration;

        public void Save(PluginConfiguration configuration) => Configuration = configuration;
    }
}
