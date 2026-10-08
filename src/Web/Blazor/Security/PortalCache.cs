using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace NexaVerify.Web.Security;

/// <summary>Where the portal keeps shared state: sessions, pending MFA sign-ins and (softly) the export throttle.</summary>
public enum PortalCacheProvider
{
    /// <summary>This process's memory: single node or sticky sessions (needs <c>Session:AllowInMemoryStore</c> outside Development).</summary>
    Memory = 0,

    /// <summary>A SQL Server table (<c>Microsoft.Extensions.Caching.SqlServer</c>) shared by every portal node.</summary>
    SqlServer,

    /// <summary>Redis (<c>Microsoft.Extensions.Caching.StackExchangeRedis</c>) shared by every portal node.</summary>
    Redis,
}

/// <summary>
/// <c>PortalCache:*</c> (deploy/CONFIG.md). Everything stored is encrypted with Data Protection by the stores themselves, so the cache
/// server never sees tokens; the connection string is still a secret and belongs in a secret store.
/// </summary>
public sealed class PortalCacheOptions
{
    public const string Section = "PortalCache";

    public PortalCacheProvider Provider { get; set; } = PortalCacheProvider.Memory;

    public SqlServerCacheSettings SqlServer { get; set; } = new();

    public RedisCacheSettings Redis { get; set; } = new();

    public bool IsShared => Provider != PortalCacheProvider.Memory;
}

public sealed class SqlServerCacheSettings
{
    /// <summary>Connection to the database that holds the cache table (preferably a small database of its own, with a login that can only use that table).</summary>
    public string? ConnectionString { get; set; }

    [RegularExpression(PortalCacheRegistration.IdentifierPattern)]
    public string SchemaName { get; set; } = "dbo";

    [RegularExpression(PortalCacheRegistration.IdentifierPattern)]
    public string TableName { get; set; } = "PortalCache";

    /// <summary>Create the table at start-up if it is missing (needs DDL rights). Off: create it from deploy/sql/portal-cache.sql.</summary>
    public bool EnsureTable { get; set; }

    /// <summary>How often expired rows are deleted.</summary>
    [Range(1, 1440)]
    public int ExpiredItemsDeletionIntervalMinutes { get; set; } = 30;
}

public sealed class RedisCacheSettings
{
    public string? Configuration { get; set; }

    [StringLength(50)]
    public string InstanceName { get; set; } = "nexaverify-portal:";
}

public static class PortalCacheRegistration
{
    public const string IdentifierPattern = "^[A-Za-z_][A-Za-z0-9_]{0,63}$";

    private static readonly Regex Identifier = new(IdentifierPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Registers the <see cref="IDistributedCache"/> for the chosen provider and refuses an incomplete configuration at start-up.</summary>
    public static IServiceCollection AddPortalCache(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PortalCacheOptions.Section).Get<PortalCacheOptions>() ?? new PortalCacheOptions();
        Validate(options);
        services.AddOptions<PortalCacheOptions>().Bind(configuration.GetSection(PortalCacheOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        switch (options.Provider)
        {
            case PortalCacheProvider.SqlServer:
                services.AddDistributedSqlServerCache(o =>
                {
                    o.ConnectionString = options.SqlServer.ConnectionString;
                    o.SchemaName = options.SqlServer.SchemaName;
                    o.TableName = options.SqlServer.TableName;
                    o.ExpiredItemsDeletionInterval = TimeSpan.FromMinutes(options.SqlServer.ExpiredItemsDeletionIntervalMinutes);
                });
                if (options.SqlServer.EnsureTable)
                {
                    services.AddHostedService<SqlCacheTableInitializer>();
                }

                break;
            case PortalCacheProvider.Redis:
                services.AddStackExchangeRedisCache(o =>
                {
                    o.Configuration = options.Redis.Configuration;
                    o.InstanceName = options.Redis.InstanceName;
                });
                break;
            default:
                services.AddDistributedMemoryCache();
                break;
        }

        return services;
    }

    public static void Validate(PortalCacheOptions options)
    {
        if (!Enum.IsDefined(options.Provider))
        {
            throw new InvalidOperationException("PortalCache:Provider must be Memory, SqlServer or Redis.");
        }

        if (options.Provider == PortalCacheProvider.SqlServer)
        {
            if (string.IsNullOrWhiteSpace(options.SqlServer.ConnectionString))
            {
                throw new InvalidOperationException("PortalCache:SqlServer:ConnectionString is required when PortalCache:Provider is SqlServer.");
            }

            if (!Identifier.IsMatch(options.SqlServer.SchemaName) || !Identifier.IsMatch(options.SqlServer.TableName))
            {
                throw new InvalidOperationException("PortalCache:SqlServer:SchemaName/TableName must be plain identifiers (letters, digits, underscore).");
            }
        }

        if (options.Provider == PortalCacheProvider.Redis && string.IsNullOrWhiteSpace(options.Redis.Configuration))
        {
            throw new InvalidOperationException("PortalCache:Redis:Configuration is required when PortalCache:Provider is Redis.");
        }
    }

    /// <summary>The DDL <c>dotnet sql-cache create</c> would run (also shipped as deploy/sql/portal-cache.sql). Identifiers must already be validated.</summary>
    public static string CreateTableSql(string schema, string table) => $"""
        IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');
        IF OBJECT_ID(N'[{schema}].[{table}]', N'U') IS NULL
        BEGIN
            CREATE TABLE [{schema}].[{table}](
                Id nvarchar(449) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
                Value varbinary(MAX) NOT NULL,
                ExpiresAtTime datetimeoffset NOT NULL,
                SlidingExpirationInSeconds bigint NULL,
                AbsoluteExpiration datetimeoffset NULL,
                CONSTRAINT [pk_{table}_Id] PRIMARY KEY (Id));
            CREATE NONCLUSTERED INDEX [Index_{table}_ExpiresAtTime] ON [{schema}].[{table}](ExpiresAtTime);
        END
        """;
}

/// <summary>Creates the SQL cache table when <c>PortalCache:SqlServer:EnsureTable</c> is on. Idempotent, so every node may run it.</summary>
public sealed class SqlCacheTableInitializer(IOptions<PortalCacheOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.SqlServer;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = PortalCacheRegistration.CreateTableSql(settings.SchemaName, settings.TableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Cluster-wide (soft) variant of the export throttle: the window lives in the shared cache so every node sees the same count.
/// <see cref="IDistributedCache"/> has no atomic increment, so two simultaneous requests can both pass — the hard, atomic cap is the
/// API's per-principal export throttle (<c>Throttle:ExportsPerMinute</c>), which the portal relays as 429.
/// </summary>
internal static class SharedDownloadWindow
{
    public static bool TryAcquire(IDistributedCache cache, string key, DateTimeOffset now, TimeSpan window, int max, out TimeSpan retryAfter)
    {
        var cacheKey = "nv:download:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..32];
        var start = now;
        var count = 0;
        if (cache.GetString(cacheKey) is { } stored
            && stored.Split('|') is [var ticks, var used]
            && long.TryParse(ticks, out var startTicks) && int.TryParse(used, out var usedCount)
            && now - new DateTimeOffset(startTicks, TimeSpan.Zero) < window)
        {
            start = new DateTimeOffset(startTicks, TimeSpan.Zero);
            count = usedCount;
        }

        if (count >= max)
        {
            retryAfter = start + window - now;
            return false;
        }

        cache.SetString(
            cacheKey,
            $"{start.UtcTicks}|{count + 1}",
            new DistributedCacheEntryOptions { AbsoluteExpiration = start + window });
        retryAfter = TimeSpan.Zero;
        return true;
    }
}
