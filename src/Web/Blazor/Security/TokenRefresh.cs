using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

public enum RefreshStatus
{
    /// <summary>A usable access token is now stored (we refreshed, or another request already had).</summary>
    Refreshed,

    /// <summary>The API refused the refresh token (expired, revoked, or reuse detected): the session was ended.</summary>
    SessionEnded,

    /// <summary>The API could not be reached or failed; the session is kept and the caller may try again later.</summary>
    Unavailable,
}

public sealed record RefreshOutcome(RefreshStatus Status, string? AccessToken = null);

/// <summary>Exchanges a refresh token for a new token pair (POST /auth/refresh) without any session handling.</summary>
public interface IRefreshTokenExchange
{
    Task<ApiResult<LoginResponse>> ExchangeAsync(string refreshToken, CancellationToken ct);
}

public sealed class HttpRefreshTokenExchange(IHttpClientFactory httpClients) : IRefreshTokenExchange
{
    public async Task<ApiResult<LoginResponse>> ExchangeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var client = httpClients.CreateClient(ApiClientNames.Anonymous);
            using var response = await client.PostAsJsonAsync("auth/refresh", new RefreshRequest(refreshToken), ApiGateway.Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<LoginResponse>.Fail(await ProblemMapper.FromResponseAsync(response, ct));
            }

            var body = await response.Content.ReadFromJsonAsync<LoginResponse>(ApiGateway.Json, ct);
            return body is null ? ApiResult<LoginResponse>.Fail(ApiError.Unexpected()) : ApiResult<LoginResponse>.Ok(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return ApiResult<LoginResponse>.Fail("API_UNAVAILABLE", "The service is not reachable.", null, 503);
        }
    }
}

/// <summary>
/// Refreshes access tokens transparently and exactly once per expiry per session ("single flight"): concurrent requests of one
/// session queue behind one lock, and everyone who arrives after the first refresh simply picks up the new token. That matters
/// because the API rotates refresh tokens and treats a replayed one as theft (it revokes the whole token family) - two parallel
/// refreshes with the same token would sign the user out.
/// </summary>
public sealed class TokenRefreshCoordinator(
    ISessionStore store,
    IRefreshTokenExchange exchange,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<SessionOptions> options,
    ILogger<TokenRefreshCoordinator> logger)
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <param name="sessionId">The session to refresh.</param>
    /// <param name="staleAccessToken">The token the caller just used (or null if it had none). If the stored token differs, someone already refreshed.</param>
    public async Task<RefreshOutcome> RefreshAsync(string sessionId, string? staleAccessToken, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var session = await store.GetAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                _locks.TryRemove(sessionId, out _);
                return new RefreshOutcome(RefreshStatus.SessionEnded);
            }

            var now = clock.GetUtcNow();
            var skew = TimeSpan.FromSeconds(options.Value.RefreshSkewSeconds);
            var someoneElseRefreshed = staleAccessToken is not null && !string.Equals(session.AccessToken, staleAccessToken, StringComparison.Ordinal);
            if (someoneElseRefreshed && session.AccessTokenExpiresAt - skew > now)
            {
                return new RefreshOutcome(RefreshStatus.Refreshed, session.AccessToken);
            }

            // Once the refresh token has left for the API the rotation must be recorded even if the caller gives up:
            // the exchange and the store update are therefore not tied to the caller's cancellation token.
            using var timeout = new CancellationTokenSource(ExchangeTimeout);
            var result = await exchange.ExchangeAsync(session.RefreshToken, timeout.Token);

            if (!result.IsSuccess)
            {
                if (result.Error!.Status is >= 400 and < 500 and not 429 and not 408)
                {
                    // Expired / revoked / reuse detected. The API already revoked the family; we drop our copy.
                    logger.LogWarning("Refresh rejected by the API (status {Status}, code {Code}); ending session.", result.Error.Status, result.Error.Code);
                    await store.RemoveAsync(sessionId, CancellationToken.None);
                    _locks.TryRemove(sessionId, out _);
                    return new RefreshOutcome(RefreshStatus.SessionEnded);
                }

                logger.LogWarning("Token refresh failed transiently (status {Status}).", result.Error.Status);
                return new RefreshOutcome(RefreshStatus.Unavailable);
            }

            var tokens = result.Value;
            var updated = await store.UpdateAsync(sessionId, s => s with
            {
                AccessToken = tokens.AccessToken,
                AccessTokenExpiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn),
                RefreshToken = tokens.RefreshToken,
                MustChangePassword = tokens.MustChangePassword,
            }, CancellationToken.None);

            return updated is null
                ? new RefreshOutcome(RefreshStatus.SessionEnded)
                : new RefreshOutcome(RefreshStatus.Refreshed, updated.AccessToken);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Adds the session's bearer token to API calls and refreshes it when needed. Runs inside the HttpClient pipeline, so the
/// session id travels as a request option (handler scopes are not the circuit's scope).
/// </summary>
public sealed class SessionBearerHandler(ISessionStore store, TokenRefreshCoordinator refresher, TimeProvider clock, Microsoft.Extensions.Options.IOptions<SessionOptions> options)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(ApiGateway.SessionKey, out var sessionId) || string.IsNullOrEmpty(sessionId))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var session = await store.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionEnded(request);
        }

        var token = session.AccessToken;
        var skew = TimeSpan.FromSeconds(options.Value.RefreshSkewSeconds);
        if (session.AccessTokenExpiresAt - skew <= clock.GetUtcNow())
        {
            var proactive = await refresher.RefreshAsync(sessionId, token, cancellationToken);
            if (proactive.Status == RefreshStatus.SessionEnded)
            {
                return SessionEnded(request);
            }

            token = proactive.AccessToken ?? token; // Unavailable: try the old token, the API decides
        }

        await store.TouchAsync(sessionId, cancellationToken);

        // Buffer the body once so a refresh-and-retry can replay it.
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentHeaders = request.Content?.Headers.ToList();

        var response = await SendWithAsync(request, token, body, contentHeaders, cancellationToken, clone: false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var outcome = await refresher.RefreshAsync(sessionId, token, cancellationToken);
        switch (outcome.Status)
        {
            case RefreshStatus.Refreshed:
                response.Dispose();
                return await SendWithAsync(request, outcome.AccessToken!, body, contentHeaders, cancellationToken, clone: true);
            case RefreshStatus.SessionEnded:
                response.Dispose();
                return SessionEnded(request);
            default:
                return response;
        }
    }

    private async Task<HttpResponseMessage> SendWithAsync(
        HttpRequestMessage original, string token, byte[]? body, List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders, CancellationToken ct, bool clone)
    {
        var message = original;
        if (clone)
        {
            message = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
            foreach (var header in original.Headers)
            {
                if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
                foreach (var header in contentHeaders ?? [])
                {
                    message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await base.SendAsync(message, ct);
        }
        finally
        {
            if (clone)
            {
                message.Dispose();
            }
        }
    }

    private static HttpResponseMessage SessionEnded(HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
        response.Headers.TryAddWithoutValidation(ApiGateway.SessionEndedHeader, "1");
        return response;
    }
}
