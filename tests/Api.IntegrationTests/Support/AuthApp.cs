using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests.Support;

public sealed class CapturingEmailSender : IEmailOutbox
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

    public void Enqueue(EmailMessage message)
    {
        lock (_sent)
        {
            _sent.Add(message);
        }
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

    public static async Task<AuthApp> CreateAsync(SqlServerFixture fixture, IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(connectionString);

        var emails = new CapturingEmailSender();
        var factory = new ApiFactory
        {
            ConnectionString = connectionString,
            UseTestAuth = false,
            Settings = settings ?? new Dictionary<string, string>(),
            ConfigureServices = services =>
            {
                services.AddSingleton<IEmailOutbox>(emails);
                configure?.Invoke(services);
            },
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

        if (!await db.Clients.AnyAsync(c => c.Id == clientId))
        {
            var client = NexaVerify.Domain.Tenancy.Client.Create("T" + clientId.ToString("N")[..12], "Test client " + clientId.ToString("N")[..6], "ops@client.test", "UTC", DateTime.UtcNow);
            typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(client, clientId);
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

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

    /// <summary>Resolves services in a fresh scope bound to a tenant (or the platform when <paramref name="clientId"/> is null), like a request would.</summary>
    public async Task<T> WithServicesAsync<T>(Guid? clientId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>();
        using var bound = clientId is { } id ? tenant.BeginTenant(id) : tenant.BeginPlatform("test: service access");
        return await action(scope.ServiceProvider);
    }

    /// <summary>Posts a multipart form with an <c>image</c> part and the given text fields (the face API's wire format).</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string path, byte[]? image, IReadOnlyDictionary<string, string>? fields, string token, string? idempotencyKey = null, string imageType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? new Dictionary<string, string>())
        {
            form.Add(new StringContent(value), key);
        }

        if (image is not null)
        {
            var part = new ByteArrayContent(image);
            part.Headers.ContentType = new MediaTypeHeaderValue(imageType);
            form.Add(part, "image", "upload.bin");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await SendAsync(request, token);
    }

    /// <summary>Inserts an active license directly, with its Grant ledger row.</summary>
    public Task<Guid> SeedLicenseAsync(Guid clientId, int credits, DateTime? startsAt = null, DateTime? expiresAt = null) =>
        WithServicesAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var license = NexaVerify.Domain.Licensing.License.Create(
                clientId, "Seeded " + Guid.NewGuid().ToString("N")[..6], null, NexaVerify.Application.Licensing.LicenseKeyGenerator.Generate(), credits,
                startsAt ?? DateTime.UtcNow.AddDays(-1), expiresAt ?? DateTime.UtcNow.AddDays(60));
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            await sp.GetRequiredService<NexaVerify.Application.Licensing.LedgerWriter>()
                .AppendAsync(license, NexaVerify.Domain.Licensing.LedgerEntryType.Grant, credits, 0, default, reason: "seed");
            await db.SaveChangesAsync();
            return license.Id;
        });

    public Task<HttpResponseMessage> DeleteAsync(string path, string? token = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Delete, path), token);

    /// <summary>Extracts (email, token) from the https link inside a captured invitation / reset email.</summary>
    public static (string Email, string Token) LinkFrom(EmailMessage mail)
    {
        var link = new Uri(mail.Body.Split('\n').Single(l => l.StartsWith("https://", StringComparison.Ordinal)));
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        return (query["email"]!, query["token"]!);
    }

    /// <summary>Creates a client through the admin API, completes the invitation like the real admin would, and returns a signed-in ClientAdmin.</summary>
    public async Task<(ClientDto Client, LoginResponse Admin)> OnboardClientAsync(
        string platformToken, string code, string adminEmail, string password = StrongPassword)
    {
        var before = Emails.Sent.Count;
        var created = await PostAsync("/api/v1/admin/clients", NewClientRequest(code, adminEmail), platformToken);
        created.EnsureSuccessStatusCode();
        var client = (await created.Content.ReadFromJsonAsync<ClientDto>(Json))!;
        var (email, token) = LinkFrom(Emails.Sent[before]);
        (await PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, password))).EnsureSuccessStatusCode();
        return (client, await LoginAsync(adminEmail, password));
    }

    public static CreateClientRequest NewClientRequest(string code, string adminEmail) => new(
        code, "Acme " + code, "Acme Corporation Ltd", "contact@" + code.ToLowerInvariant() + ".test", "+44 20 7946 0000",
        "1 High Street", null, "London", null, "EC1A 1BB", "GB", "https://" + code.ToLowerInvariant() + ".test", "Retail", "Europe/London", null,
        adminEmail, "Ada Admin");

    /// <summary>Runs <paramref name="action"/> exactly as a request of that tenant would (tenant scope, not platform).</summary>
    public async Task<T> WithTenantDbAsync<T>(Guid clientId, Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>();
        using var tenantScope = tenant.BeginTenant(clientId);
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
        await SqlServerFixture.TryDropDatabaseAsync(ConnectionString);
    }
}
