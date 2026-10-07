using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests;

[CollectionDefinition(SqlServerCollection.Name)]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>
{
}
