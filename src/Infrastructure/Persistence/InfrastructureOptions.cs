using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Command timeout in seconds for queries issued through EF Core.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}
