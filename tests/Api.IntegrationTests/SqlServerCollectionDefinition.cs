using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[CollectionDefinition(SqlServerCollection.Name)]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>
{
}
