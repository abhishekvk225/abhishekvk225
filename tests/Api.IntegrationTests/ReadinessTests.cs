using System.Net;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class ReadinessTests
{
    private readonly SqlServerFixture _fixture;

    public ReadinessTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Readiness_is_unavailable_for_a_reachable_but_unmigrated_database()
    {
        await using var factory = new ApiFactory { ConnectionString = await _fixture.CreateDatabaseAsync() };
        var client = factory.CreateClient();

        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_is_ok_once_migrated_guarded_and_seeded()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(connectionString);
        await using var factory = new ApiFactory { ConnectionString = connectionString };
        var client = factory.CreateClient();

        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_turns_unavailable_if_the_security_policy_is_dropped_behind_the_apps_back()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(connectionString);
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP SECURITY POLICY [security].[TenantPolicy]";
            await command.ExecuteNonQueryAsync();
        }

        await using var factory = new ApiFactory { ConnectionString = connectionString };

        (await factory.CreateClient().GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
