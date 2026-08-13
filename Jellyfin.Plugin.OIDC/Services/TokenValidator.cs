using System;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OIDC.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.OIDC.Services;

/// <summary>
/// Thrown when a token from the identity provider fails cryptographic or claim validation.
/// The message is safe to surface to the user; details are logged separately.
/// </summary>
public sealed class TokenValidationFailedException : Exception
{
    public TokenValidationFailedException(string message)
        : base(message)
    {
    }

    public TokenValidationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Validates JWTs issued by a configured provider against the signing keys published at its
/// JWKS endpoint.
///
/// The authorization-code flow fetches tokens over TLS straight from the discovered token
/// endpoint, which is why OIDC Core 3.1.3.7 permits a client to skip signature verification.
/// We verify anyway: the ID token drives account provisioning and the administrator flag, so
/// TLS to the IdP should not be the only thing standing between a forged claim and an admin
/// session. Verifying also gets us the checks that actually bite in practice — audience
/// (a token minted for a different client in the same realm is rejected) and expiry.
///
/// Discovery documents and signing keys are cached per authority and refreshed automatically,
/// so steady-state logins add no extra round trips to the IdP.
/// </summary>
public sealed class TokenValidator
{
    /// <summary>
    /// Asymmetric algorithms only. Restricting the set keeps a provider from downgrading us to
    /// an HMAC algorithm, where the (attacker-visible) public key or the client secret could be
    /// used as the signing key. "none" is rejected by RequireSignedTokens regardless.
    /// </summary>
    private static readonly string[] AllowedAlgorithms =
    {
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
    };

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configManagers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TokenValidator> _logger;

    public TokenValidator(IHttpClientFactory httpClientFactory, ILogger<TokenValidator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fully validates an ID token: signature, issuer, audience (this client) and lifetime.
    /// Returns the parsed token on success and throws <see cref="TokenValidationFailedException"/>
    /// otherwise — callers must treat a throw as an authentication failure.
    /// </summary>
    public Task<JwtSecurityToken> ValidateIdTokenAsync(
        OidcProviderConfig provider,
        string idToken,
        CancellationToken cancellationToken = default)
    {
        return ValidateAsync(provider, idToken, validateAudience: true, cancellationToken);
    }

    /// <summary>
    /// Validates an access token for the purpose of reading supplementary claims (roles, picture)
    /// that some providers place only in the access token. Signature, issuer and lifetime are
    /// enforced; the audience is not, because an access token is minted for a resource server
    /// rather than for this client.
    ///
    /// This is not an identity assertion — the ID token remains the sole source of identity.
    /// </summary>
    public Task<JwtSecurityToken> ValidateAccessTokenForClaimsAsync(
        OidcProviderConfig provider,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return ValidateAsync(provider, accessToken, validateAudience: false, cancellationToken);
    }

    private async Task<JwtSecurityToken> ValidateAsync(
        OidcProviderConfig provider,
        string token,
        bool validateAudience,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new TokenValidationFailedException("No token was returned by the identity provider");
        }

        var configManager = GetConfigurationManager(provider.Authority);

        OpenIdConnectConfiguration config;
        try
        {
            config = await configManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new TokenValidationFailedException(
                "Could not retrieve the identity provider's signing keys", ex);
        }

        try
        {
            return Validate(token, config, provider, validateAudience);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // The IdP most likely rotated its signing keys since we last fetched the JWKS.
            // Force a refresh and try once more before failing the login.
            _logger.LogInformation(
                "Signing key not found for provider {Provider}; refreshing JWKS and retrying",
                provider.ProviderId);

            configManager.RequestRefresh();

            try
            {
                config = await configManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                return Validate(token, config, provider, validateAudience);
            }
            catch (Exception ex)
            {
                throw new TokenValidationFailedException(
                    "Token signature could not be verified against the provider's signing keys", ex);
            }
        }
        catch (SecurityTokenException ex)
        {
            throw new TokenValidationFailedException(ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            // Malformed / unreadable token.
            throw new TokenValidationFailedException("Token could not be read", ex);
        }
    }

    /// <summary>
    /// The security-critical surface: everything a forged or misdirected token has to get past.
    /// Internal so it can be exercised directly by the test suite without standing up a JWKS
    /// endpoint — the discovery/caching around it is Microsoft.IdentityModel's code.
    /// </summary>
    internal static JwtSecurityToken Validate(
        string token,
        OpenIdConnectConfiguration config,
        OidcProviderConfig provider,
        bool validateAudience)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,

            ValidateAudience = validateAudience,
            ValidAudience = validateAudience ? provider.ClientId : null,

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),

            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            RequireSignedTokens = true,
            ValidAlgorithms = AllowedAlgorithms
        };

        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, parameters, out var validated);

        if (validated is not JwtSecurityToken jwt)
        {
            throw new SecurityTokenException("Validated token was not a JWT");
        }

        return jwt;
    }

    private ConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string authority)
    {
        return _configManagers.GetOrAdd(authority, key =>
        {
            var httpClient = _httpClientFactory.CreateClient("OidcPlugin");

            // Mirror IdentityModel's discovery policy, which the rest of the plugin uses:
            // HTTPS is required except against loopback, so local test setups keep working.
            var documentRetriever = new HttpDocumentRetriever(httpClient)
            {
                RequireHttps = !IsLoopback(key)
            };

            return new ConfigurationManager<OpenIdConnectConfiguration>(
                BuildMetadataAddress(key),
                new OpenIdConnectConfigurationRetriever(),
                documentRetriever);
        });
    }

    private static string BuildMetadataAddress(string authority)
    {
        const string WellKnown = ".well-known/openid-configuration";

        var trimmed = authority.TrimEnd('/');
        if (trimmed.EndsWith(WellKnown, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/{WellKnown}";
    }

    private static bool IsLoopback(string authority)
    {
        return Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }
}
