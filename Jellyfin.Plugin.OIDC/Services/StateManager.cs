using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OIDC.Services;

public sealed class OidcState
{
    public required string ProviderId { get; init; }
    public required string Nonce { get; init; }
    public required string CodeVerifier { get; init; }
    public required string RedirectUri { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AuthorizedSession
{
    public required string ProviderId { get; init; }
    public required string Username { get; init; }
    public string? DisplayName { get; init; }
    public required string[] Roles { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class StateManager : IHostedService, IDisposable
{
    private static readonly TimeSpan StateExpiry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, OidcState> _pendingStates = new();
    private readonly ConcurrentDictionary<string, AuthorizedSession> _authorizedSessions = new();
    private readonly ILogger<StateManager> _logger;
    private Timer? _cleanupTimer;

    public StateManager(ILogger<StateManager> logger)
    {
        _logger = logger;
    }

    public string StoreState(OidcState state)
    {
        var key = Guid.NewGuid().ToString("N");
        _pendingStates[key] = state;
        return key;
    }

    public OidcState? ConsumeState(string stateKey)
    {
        if (!_pendingStates.TryRemove(stateKey, out var state))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - state.CreatedAt > StateExpiry)
        {
            _logger.LogWarning("OIDC state expired for provider {ProviderId}", state.ProviderId);
            return null;
        }

        return state;
    }

    public string StoreAuthorizedSession(AuthorizedSession session)
    {
        var token = Guid.NewGuid().ToString("N");
        _authorizedSessions[token] = session;
        return token;
    }

    public AuthorizedSession? ConsumeAuthorizedSession(string token)
    {
        if (!_authorizedSessions.TryRemove(token, out var session))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - session.CreatedAt > SessionExpiry)
        {
            _logger.LogWarning("Authorized session expired for user {Username}", session.Username);
            return null;
        }

        return session;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(Cleanup, null, CleanupInterval, CleanupInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    private void Cleanup(object? state)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (key, oidcState) in _pendingStates)
        {
            if (now - oidcState.CreatedAt > StateExpiry)
            {
                _pendingStates.TryRemove(key, out _);
            }
        }

        foreach (var (key, session) in _authorizedSessions)
        {
            if (now - session.CreatedAt > SessionExpiry)
            {
                _authorizedSessions.TryRemove(key, out _);
            }
        }
    }
}
