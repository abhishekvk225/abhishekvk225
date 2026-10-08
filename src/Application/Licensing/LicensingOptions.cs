using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Application.Licensing;

public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>
    /// Largest credit adjustment (up or down, in credits) one person can apply on their own. A bigger one becomes a request that a
    /// DIFFERENT user with <c>licenses.approve-adjust</c> must approve. Setting key: <c>Licensing:MaxAdjustPerAction</c>.
    /// </summary>
    [Range(1, 100_000_000)]
    public int MaxAdjustPerAction { get; set; } = 10_000;

    /// <summary>How long an approver has to decide before the request expires and must be raised again.</summary>
    [Range(1, 168)]
    public int AdjustApprovalHours { get; set; } = 24;
}
