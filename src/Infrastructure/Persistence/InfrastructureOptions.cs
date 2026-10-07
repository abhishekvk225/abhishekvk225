using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Command timeout in seconds for queries issued through EF Core.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// When true, readiness fails if the application's own database login is sysadmin / db_owner / can alter security policies -
    /// RLS is only a safety net if the app cannot switch it off. Enabled in Production.
    /// </summary>
    public bool RequireLeastPrivilege { get; set; }
}
