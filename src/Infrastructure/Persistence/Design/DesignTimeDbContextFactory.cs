using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Persistence.Design;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without running the host. The connection string is only used when a command
/// needs a database (e.g. <c>database update</c>); it comes from NEXAVERIFY_CONNECTION, never from source.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("NEXAVERIFY_CONNECTION")
            ?? "Server=127.0.0.1,1;Database=NexaVerifyDesign;User Id=design;Password=design;Encrypt=False";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options, new DesignTimeTenant());
    }

    private sealed class DesignTimeTenant : ITenantContext
    {
        public Guid? ClientId => null;

        public bool IsPlatform => true;
    }
}
