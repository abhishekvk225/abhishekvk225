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
    public async Task Readiness_is_ok_when_the_database_is_reachable()
    {
        await using var factory = new ApiFactory { ConnectionString = await _fixture.CreateDatabaseAsync() };
        var client = factory.CreateClient();

        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
