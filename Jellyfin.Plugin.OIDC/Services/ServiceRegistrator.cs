using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OIDC.Services;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StateManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StateManager>());

        // Singleton so discovery documents and JWKS stay cached across logins.
        serviceCollection.AddSingleton<TokenValidator>();

        serviceCollection.AddSingleton<IConfigurationStore, PluginConfigurationStore>();

        // Singleton so its write lock actually serialises concurrent link writes.
        serviceCollection.AddSingleton<UserLinkStore>();

        serviceCollection.AddScoped<UserResolver>();
        serviceCollection.AddScoped<RbacService>();
        serviceCollection.AddScoped<ProfileImageService>();
        serviceCollection.AddScoped<UserSyncService>();
    }
}
