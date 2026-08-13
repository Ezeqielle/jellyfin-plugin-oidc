using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Jellyfin.Plugin.OIDC.Configuration;
using Jellyfin.Plugin.OIDC.Services;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Jellyfin.Plugin.OIDC.Tests;

/// <summary>
/// Covers what an attacker has to get past to have a token accepted. Each test forges a token
/// that differs from a good one in exactly one way, and asserts it is rejected.
/// </summary>
public class TokenValidatorTests
{
    private const string Issuer = "https://keycloak.example.com/realms/test";
    private const string ClientId = "jellyfin";

    private static readonly RSA ProviderKey = RSA.Create(2048);
    private static readonly RSA AttackerKey = RSA.Create(2048);

    private static OidcProviderConfig Provider => new()
    {
        ProviderId = "keycloak",
        Authority = Issuer,
        ClientId = ClientId
    };

    private static OpenIdConnectConfiguration Configuration
    {
        get
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(new RsaSecurityKey(ProviderKey) { KeyId = "provider-key" });
            return config;
        }
    }

    private static string CreateToken(
        RSA? signingKey = null,
        string issuer = Issuer,
        string audience = ClientId,
        DateTime? expires = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var key = new RsaSecurityKey(signingKey ?? ProviderKey) { KeyId = "provider-key" };

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            // Keep nbf ahead of exp even when the caller backdates expiry to forge a stale token.
            NotBefore = expiry.AddMinutes(-10),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(key, algorithm),
            Claims = new Dictionary<string, object> { ["sub"] = "user-123" }
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static JwtSecurityToken Validate(string token, bool validateAudience = true)
    {
        return TokenValidator.Validate(token, Configuration, Provider, validateAudience);
    }

    [Fact]
    public void ValidToken_IsAccepted()
    {
        var jwt = Validate(CreateToken());

        Assert.Equal(Issuer, jwt.Issuer);
        Assert.Equal("user-123", jwt.Claims.First(c => c.Type == "sub").Value);
    }

    [Fact]
    public void TokenSignedByAnotherKey_IsRejected()
    {
        // The core of the vulnerability this validator exists to close: before it, a token
        // signed by anybody at all was parsed and trusted.
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(CreateToken(signingKey: AttackerKey)));
    }

    [Fact]
    public void UnsignedToken_IsRejected()
    {
        // "alg": "none" — header and payload with an empty signature segment.
        var unsigned = new JwtSecurityToken(
            issuer: Issuer,
            audience: ClientId,
            claims: new[] { new System.Security.Claims.Claim("sub", "user-123") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10));

        var token = new JwtSecurityTokenHandler().WriteToken(unsigned);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void TokenWithTamperedPayload_IsRejected()
    {
        var parts = CreateToken().Split('.');
        var forgedPayload = Base64UrlEncoder.Encode(
            """{"sub":"admin","iss":"https://keycloak.example.com/realms/test","aud":"jellyfin","exp":33267600000}""");

        var tampered = $"{parts[0]}.{forgedPayload}.{parts[2]}";

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(tampered));
    }

    [Fact]
    public void TokenForAnotherClient_IsRejected()
    {
        // A token the IdP legitimately minted for a different client in the same realm.
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(CreateToken(audience: "grafana")));
    }

    [Fact]
    public void TokenFromAnotherIssuer_IsRejected()
    {
        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(CreateToken(issuer: "https://evil.example.com/realms/test")));
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        // Well past the 2-minute clock skew allowance.
        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(CreateToken(expires: DateTime.UtcNow.AddHours(-1))));
    }

    [Fact]
    public void HmacSignedToken_IsRejected()
    {
        // Algorithm-confusion attempt: sign with HMAC so a value the attacker may know
        // (the public key, or the client secret) becomes the signing key.
        var secret = new SymmetricSecurityKey(new byte[32]);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(secret, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = "user-123" }
        };

        var token = new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void AccessTokenPath_SkipsAudienceButStillVerifiesSignature()
    {
        // Access tokens are minted for a resource server, so their audience is not ours. We
        // still read role claims from them, so the signature must hold.
        var forResourceServer = CreateToken(audience: "account");
        var jwt = Validate(forResourceServer, validateAudience: false);
        Assert.Equal(Issuer, jwt.Issuer);

        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(CreateToken(signingKey: AttackerKey, audience: "account"), validateAudience: false));
    }

    [Fact]
    public void GarbageInput_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Validate("not-a-jwt"));
        Assert.ThrowsAny<Exception>(() => Validate(string.Empty));
    }
}
