using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Authorization;
using NexaVerify.Contracts.Common;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.Services;

/// <summary>Named HttpClients used by the portal (see Program.cs).</summary>
public static class ApiClientNames
{
    /// <summary>Attaches the session's bearer token, refreshes on 401. Server-side only.</summary>
    public const string Authenticated = "NexaApi";

    /// <summary>No credentials: login, refresh, forgot/reset password.</summary>
    public const string Anonymous = "NexaApiAnonymous";
}

/// <summary>How a call authenticates. Default = the signed-in session of the current user.</summary>
public sealed record ApiCallOptions
{
    /// <summary>No Authorization header at all (login, refresh, password reset).</summary>
    public bool Anonymous { get; init; }

    /// <summary>An explicit bearer token (used right after login, before a session exists).</summary>
    public string? BearerToken { get; init; }

    /// <summary>Act for this session instead of the circuit's (used by BFF endpoints that have the HttpContext).</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The end user's address, sent as <c>X-Forwarded-For</c> so the API's per-IP limits and lockout history see the person, not the
    /// portal. The API only honours it from proxies listed in its <c>ForwardedHeaders:KnownProxies</c>.
    /// </summary>
    public string? ClientIp { get; init; }

    /// <summary>Operations that legitimately take long (a full ledger scan) wait for <c>Api:LongRunningTimeoutSeconds</c> instead of <c>Api:TimeoutSeconds</c>.</summary>
    public bool LongRunning { get; init; }

    /// <summary>
    /// Background work (a poll the user did not trigger) must not count as activity: it does not slide the idle-session window, so an
    /// unattended tab still times out.
    /// </summary>
    public bool Passive { get; init; }

    /// <summary>Extra request headers (for example <c>Idempotency-Key</c>). Never used for credentials.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public static ApiCallOptions None { get; } = new() { Anonymous = true };
}

/// <summary>Resolves the id of the session the current UI scope belongs to.</summary>
public interface ICurrentSession
{
    Task<string?> GetSessionIdAsync();
}

public sealed class AuthenticationStateCurrentSession(AuthenticationStateProvider provider) : ICurrentSession
{
    public async Task<string?> GetSessionIdAsync()
    {
        var state = await provider.GetAuthenticationStateAsync();
        return state.User.FindFirst(PortalClaims.SessionId)?.Value;
    }
}

/// <summary>
/// The single door from the UI to the API. Turns every outcome - success, problem+json, timeouts, dropped connections - into an
/// <see cref="ApiResult{T}"/>; callers never see exceptions, raw HTTP or tokens.
/// </summary>
public interface IApiGateway
{
    Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken ct = default, ApiCallOptions? options = null);

    Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct = default, ApiCallOptions? options = null);

    /// <summary>For calls with no response body (204/202).</summary>
    Task<ApiResult<bool>> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, ApiCallOptions? options = null);

    /// <summary>Opens a response for streaming (CSV). The caller disposes the returned message.</summary>
    Task<ApiResult<HttpResponseMessage>> OpenStreamAsync(string path, CancellationToken ct = default, ApiCallOptions? options = null);
}

/// <summary>
/// The address of the person using this circuit (set once from the initial HTTP request). Forwarded to the API as
/// <c>X-Forwarded-For</c> so per-IP limits and login history see the person rather than the portal.
/// </summary>
public sealed class ClientAddress
{
    public string? Value { get; set; }
}

public sealed class ApiGateway(
    IHttpClientFactory httpClients,
    ICurrentSession currentSession,
    ClientAddress? clientAddress = null,
    Microsoft.Extensions.Options.IOptions<NexaVerify.Web.Security.ApiClientOptions>? apiOptions = null) : IApiGateway
{
    public const string ForwardedForHeader = "X-Forwarded-For";

    public static readonly HttpRequestOptionsKey<string> SessionKey = new("nv.session-id");

    /// <summary>Set for passive (background) calls: the token handler then leaves the session's idle clock alone.</summary>
    public static readonly HttpRequestOptionsKey<bool> PassiveKey = new("nv.passive");

    /// <summary>Lets the token handler rebuild an upload body for a retry without keeping a second copy of it in memory.</summary>
    public static readonly HttpRequestOptionsKey<Func<HttpContent>> ContentFactoryKey = new("nv.content-factory");

    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken ct = default, ApiCallOptions? options = null) =>
        SendAsync<T>(HttpMethod.Get, path, null, ct, options);

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct = default, ApiCallOptions? options = null)
    {
        var (response, error) = await ExecuteAsync(method, path, body, HttpCompletionOption.ResponseContentRead, ct, options);
        if (error is not null)
        {
            return ApiResult<T>.Fail(error);
        }

        using (response)
        {
            try
            {
                var value = await response!.Content.ReadFromJsonAsync<T>(Json, ct);
                return value is null ? ApiResult<T>.Fail(ApiError.Unexpected(CorrelationOf(response))) : ApiResult<T>.Ok(value);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
                return ApiResult<T>.Fail(ApiError.Unexpected(CorrelationOf(response!)));
            }
        }
    }

    public async Task<ApiResult<bool>> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, ApiCallOptions? options = null)
    {
        var (response, error) = await ExecuteAsync(method, path, body, HttpCompletionOption.ResponseContentRead, ct, options);
        if (error is not null)
        {
            return ApiResult<bool>.Fail(error);
        }

        response!.Dispose();
        return ApiResult<bool>.Ok(true);
    }

    public async Task<ApiResult<HttpResponseMessage>> OpenStreamAsync(string path, CancellationToken ct = default, ApiCallOptions? options = null)
    {
        var (response, error) = await ExecuteAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseHeadersRead, ct, options);
        return error is not null ? ApiResult<HttpResponseMessage>.Fail(error) : ApiResult<HttpResponseMessage>.Ok(response!);
    }

    private async Task<(HttpResponseMessage? Response, ApiError? Error)> ExecuteAsync(
        HttpMethod method, string path, object? body, HttpCompletionOption completion, CancellationToken ct, ApiCallOptions? options)
    {
        options ??= new ApiCallOptions();
        var effectiveIp = options.ClientIp ?? clientAddress?.Value;
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is Func<HttpContent> contentFactory)
        {
            // Multipart uploads: built on demand, so a retry after a token refresh makes a fresh body instead of copying the old one.
            request.Content = contentFactory();
            request.Options.Set(ContentFactoryKey, contentFactory);
        }
        else if (body is HttpContent content)
        {
            // The caller built the content; it is disposed with the request.
            request.Content = content;
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (options.Headers is not null)
        {
            foreach (var (name, value) in options.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!string.IsNullOrEmpty(effectiveIp))
        {
            request.Headers.TryAddWithoutValidation(ForwardedForHeader, effectiveIp);
        }

        string clientName;
        if (options.Anonymous)
        {
            clientName = ApiClientNames.Anonymous;
        }
        else if (!string.IsNullOrEmpty(options.BearerToken))
        {
            clientName = ApiClientNames.Anonymous;
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.BearerToken);
        }
        else
        {
            var sessionId = options.SessionId ?? await currentSession.GetSessionIdAsync();
            if (string.IsNullOrEmpty(sessionId))
            {
                return (null, SessionExpiredError());
            }

            clientName = ApiClientNames.Authenticated;
            request.Options.Set(SessionKey, sessionId);
            if (options.Passive)
            {
                request.Options.Set(PassiveKey, true);
            }
        }

        // The HttpClient has no timeout of its own: each call gets one here, so slow operations can ask for more and a timeout can be
        // told apart from an unreachable service.
        var configured = apiOptions?.Value ?? new NexaVerify.Web.Security.ApiClientOptions();
        var limit = TimeSpan.FromSeconds(options.LongRunning ? configured.LongRunningTimeoutSeconds : configured.TimeoutSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);

        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(clientName).SendAsync(request, completion, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, new ApiError(TimeoutCode, "This is taking longer than expected. It may still be running on our side, so check again in a few minutes before trying twice.", null, 504));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return (null, new ApiError("API_UNAVAILABLE", "We can't reach the service right now. Please try again in a moment.", null, 503));
        }

        if (response.IsSuccessStatusCode)
        {
            return (response, null);
        }

        var error = await ProblemMapper.FromResponseAsync(response, ct);
        response.Dispose();
        return (null, error);
    }

    private static string? CorrelationOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(HttpHeaderNames.CorrelationId, out var values) ? values.FirstOrDefault() : null;

    internal static ApiError SessionExpiredError() =>
        new(SessionExpiredCode, "Your session has ended. Please sign in again.", null, (int)HttpStatusCode.Unauthorized);

    public const string SessionExpiredCode = "SESSION_EXPIRED";

    public const string TimeoutCode = "API_TIMEOUT";

    /// <summary>Marks a response the handler synthesised because the session is gone (not a real API answer).</summary>
    public const string SessionEndedHeader = "X-Nv-Session-Ended";
}

/// <summary>Turns RFC 7807 responses into user-friendly <see cref="ApiError"/>s. Raw exception text never reaches users.</summary>
public static class ProblemMapper
{
    private const int MaxDetailLength = 300;

    public static async Task<ApiError> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        string? detail = null;
        string? correlation = response.Headers.TryGetValues(HttpHeaderNames.CorrelationId, out var headerValues) ? headerValues.FirstOrDefault() : null;
        Dictionary<string, string[]>? fields = null;
        var status = (int)response.StatusCode;

        if (response.Headers.Contains(ApiGateway.SessionEndedHeader))
        {
            return ApiGateway.SessionExpiredError();
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body) && body.Length < 64_000)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = doc.RootElement;
                    code = ReadString(root, "code");
                    detail = ReadString(root, "detail");
                    correlation = ReadString(root, "correlationId") ?? correlation;
                    if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in errors.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.Array)
                            {
                                fields[property.Name] = property.Value.EnumerateArray()
                                    .Where(e => e.ValueKind == JsonValueKind.String)
                                    .Select(e => e.GetString()!)
                                    .Take(10)
                                    .ToArray();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            // Not a problem document (proxy error page etc.): fall back to the status.
        }

        if (status is >= 300 and < 400)
        {
            // Redirects are never followed (a body or token could be replayed to the target): treat as a misconfigured API address.
            return new ApiError("API_UNAVAILABLE", "We can't reach the service right now. Please try again in a moment.", correlation, 502);
        }

        code ??= CodeForStatus(status);
        return new ApiError(code, MessageFor(status, code, detail), correlation, status, fields is { Count: > 0 } ? fields : null);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string CodeForStatus(int status) => status switch
    {
        400 => ErrorCodes.ValidationFailed,
        401 => ErrorCodes.Unauthenticated,
        403 => ErrorCodes.Forbidden,
        404 => ErrorCodes.NotFound,
        409 => ErrorCodes.Conflict,
        429 => ErrorCodes.RateLimited,
        _ => ErrorCodes.InternalError,
    };

    /// <summary>Plain-language message. Server-authored detail is used only where the API documents it as user-safe (4xx business rules).</summary>
    public static string MessageFor(int status, string code, string? detail)
    {
        switch (code)
        {
            case ErrorCodes.RateLimited:
            case ErrorCodes.DailyQuotaExceeded:
                return "Too many requests right now. Please wait a moment and try again.";
            case ErrorCodes.Forbidden:
                return "You don't have permission to do this.";
            case ErrorCodes.NotFound:
                return "We couldn't find that. It may have been removed.";
            case ErrorCodes.ConcurrencyConflict:
                return "Someone else changed this at the same time. Reload and try again.";
            case ErrorCodes.TokenExpired:
            case ErrorCodes.Unauthenticated:
                return "Your session has ended. Please sign in again.";
        }

        if (status >= 500)
        {
            return status == 503
                ? "The service is temporarily unavailable. Please try again shortly."
                : "Something went wrong on our side. Please try again.";
        }

        if (status is 400 or 402 or 409 or 422 && !string.IsNullOrWhiteSpace(detail))
        {
            var text = detail.Trim();
            return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
        }

        return status switch
        {
            400 => "Some of the details are not valid. Please check them and try again.",
            402 => "The license does not allow this right now.",
            409 => "That conflicts with the current state. Reload and try again.",
            _ => "The request could not be completed. Please try again.",
        };
    }
}
