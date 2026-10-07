using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests.Support;

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The real API (JWT bearer, RBAC, rate limiter…) on a freshly migrated, guarded and seeded SQL Server database.</summary>
public sealed class AuthApp : IAsyncDisposable
{
    public const string StrongPassword = "Correct-Horse-Battery-9";

    private AuthApp(ApiFactory factory, string connectionString, CapturingEmailSender emails)
    {
        Factory = factory;
        ConnectionString = connectionString;
        Emails = emails;
        Client = factory.CreateClient();
    }

    public ApiFactory Factory { get; }

    public string ConnectionString { get; }

    public CapturingEmailSender Emails { get; }

    public HttpClient Client { get; }

    public static async Task<AuthApp> CreateAsync(SqlServerFixture fixture, IReadOnlyDictionary<string, string>? settings = null)
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(connectionString);

        var emails = new CapturingEmailSender();
        var factory = new ApiFactory
        {
            ConnectionString = connectionString,
            UseTestAuth = false,
            Settings = settings ?? new Dictionary<string, string>(),
            ConfigureServices = services => services.AddSingleton<IEmailSender>(emails),
        };
        return new AuthApp(factory, connectionString, emails);
    }

    public async Task<HttpResponseMessage> PostAsync(string path, object? body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        return await SendAsync(request, token);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), token);

    public Task<HttpResponseMessage> PutAsync(string path, object body, string? token = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) }, token);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? token)
    {
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await Client.SendAsync(request);
    }

    public async Task<LoginResponse> LoginAsync(string email, string password)
    {
        var response = await PostAsync("/api/v1/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
    }

    /// <summary>The seeded Super Admin, signed in with a password already changed (so permissions apply).</summary>
    public async Task<LoginResponse> SuperAdminAsync()
    {
        var first = await LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);
        if (!first.MustChangePassword)
        {
            return first;
        }

        var changed = await PostAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, StrongPassword), first.AccessToken);
        changed.EnsureSuccessStatusCode();
        return (await changed.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
    }

    /// <summary>Creates a client user directly in the database (the client-management API arrives with M3).</summary>
    public async Task<Guid> CreateClientUserAsync(Guid clientId, string email, string role, string password = StrongPassword, bool mustChange = false)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        using var platform = tenant.BeginPlatform("test: create client user");

        var user = User.Create(email, "Test " + role, hasher.Hash(password), clientId, isPlatformUser: false, mustChangePassword: mustChange);
        var roleEntity = await db.Roles.SingleAsync(r => r.Name == role);
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { ClientId = clientId, UserId = user.Id, RoleId = roleEntity.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>();
        using var platform = tenant.BeginPlatform("test: db access");
        return await action(db);
    }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}
