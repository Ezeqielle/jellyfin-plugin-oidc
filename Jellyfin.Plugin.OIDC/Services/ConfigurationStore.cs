using Jellyfin.Plugin.OIDC.Configuration;

namespace Jellyfin.Plugin.OIDC.Services;

/// <summary>
/// Read/write access to the plugin configuration.
///
/// The rest of the plugin reaches for the <see cref="OidcPlugin.Instance"/> singleton directly,
/// which is fine for read-only config lookups but makes the account-linking logic — which both
/// reads and writes — impossible to exercise without a running Jellyfin host. This interface
/// exists so that logic can be tested against an in-memory configuration.
/// </summary>
public interface IConfigurationStore
{
    PluginConfiguration Get();

    void Save(PluginConfiguration configuration);
}

/// <summary>
/// The real store, backed by Jellyfin's plugin configuration file.
/// </summary>
public sealed class PluginConfigurationStore : IConfigurationStore
{
    public PluginConfiguration Get()
    {
        return OidcPlugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    public void Save(PluginConfiguration configuration)
    {
        OidcPlugin.Instance?.UpdateConfiguration(configuration);
    }
}
