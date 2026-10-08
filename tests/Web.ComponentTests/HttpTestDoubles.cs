using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public sealed record SeenRequest(HttpMethod Method, string Path, string? Authorization, string? Body, string? SessionOption);

/// <summary>Stands in for the API: records what arrived and answers from a script.</summary>
public sealed class ScriptedApi : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<SeenRequest> _seen = [];

    public Func<SeenRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public Func<SeenRequest, Task>? Before { get; set; }

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_gate)
            {
                return [.. _seen];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body, null);
        lock (_gate)
        {
            _seen.Add(seen);
        }

        if (Before is not null)
        {
            await Before(seen);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Respond(seen);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, string? correlation = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, status >= HttpStatusCode.BadRequest ? "application/problem+json" : "application/json") };
        if (correlation is not null)
        {
            response.Headers.Add("X-Correlation-Id", correlation);
        }

        return response;
    }
}

public sealed class FakeExchange : IRefreshTokenExchange
{
    private int _calls;

    public int Calls => _calls;

    public List<string> RefreshTokensSeen { get; } = [];

    public Func<string, int, Task<ApiResult<LoginResponse>>> OnExchange { get; set; } = null!;

    public async Task<ApiResult<LoginResponse>> ExchangeAsync(string refreshToken, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls);
        lock (RefreshTokensSeen)
        {
            RefreshTokensSeen.Add(refreshToken);
        }

        return await OnExchange(refreshToken, n);
    }

    public static ApiResult<LoginResponse> Tokens(string access, string refresh, int expiresIn = 900, bool mustChange = false) =>
        ApiResult<LoginResponse>.Ok(new LoginResponse(access, "Bearer", expiresIn, refresh, mustChange,
            new UserSummary(Guid.NewGuid(), "admin@nexaverify.test", "Ada Admin", true, null, ["SuperAdmin"])));
}

public sealed class FakeHttpClientFactory(Func<string, HttpClient> create) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => create(name);
}

public sealed class FixedSession(string? id) : ICurrentSession
{
    public Task<string?> GetSessionIdAsync() => Task.FromResult(id);
}

/// <summary>The whole server-side stack wired by hand: store, coordinator, handler pipeline and gateway.</summary>
public sealed class BffHarness
{
    public BffHarness(SessionOptions? options = null, string? sessionId = "sid-1")
    {
        Options = options ?? SessionFixtures.Options;
        (Store, _, Clock) = SessionFixtures.NewStore(Options);
        Api = new ScriptedApi();
        Exchange = new FakeExchange { OnExchange = (_, n) => Task.FromResult(FakeExchange.Tokens($"access-{n + 1}", $"refresh-{n + 1}")) };
        Coordinator = new TokenRefreshCoordinator(Store, Exchange, Clock, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<TokenRefreshCoordinator>.Instance);
        Gateway = new ApiGateway(
            new FakeHttpClientFactory(name => name == ApiClientNames.Authenticated
                ? Client(new SessionBearerHandler(Store, Coordinator, Clock, Microsoft.Extensions.Options.Options.Create(Options)) { InnerHandler = Api })
                : Client(Api)),
            new FixedSession(sessionId));
    }

    public SessionOptions Options { get; }

    public DistributedSessionStore Store { get; }

    public FakeTimeProvider Clock { get; }

    public ScriptedApi Api { get; }

    public FakeExchange Exchange { get; }

    public TokenRefreshCoordinator Coordinator { get; }

    public ApiGateway Gateway { get; }

    public Task<PortalSession> SignInAsync(int accessLifetimeSeconds = 900)
    {
        var session = SessionFixtures.NewSession(Clock, accessLifetimeSeconds: accessLifetimeSeconds);
        return Store.SaveAsync(session).ContinueWith(_ => session);
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.test/api/v1/") };
}
