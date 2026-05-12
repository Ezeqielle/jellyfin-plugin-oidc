using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.OIDC.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OIDC.Api;

[ApiController]
[Route("sso/OIDC/Config")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ConfigController : ControllerBase
{
    private readonly RbacService _rbacService;

    public ConfigController(RbacService rbacService)
    {
        _rbacService = rbacService;
    }

    [HttpGet("Libraries")]
    public ActionResult<Dictionary<string, string>> GetLibraries()
    {
        return Ok(_rbacService.GetAvailableLibraries());
    }

    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var config = OidcPlugin.Instance?.Configuration;
        return Ok(new
        {
            PluginVersion = OidcPlugin.Instance?.Version?.ToString() ?? "unknown",
            ProviderCount = config?.Providers.Count ?? 0,
            RoleMappingCount = config?.RoleMappings.Count ?? 0,
            EnabledProviders = config?.Providers.Where(p => p.Enabled).Select(p => p.DisplayName).ToList()
                               ?? new List<string>()
        });
    }
}
