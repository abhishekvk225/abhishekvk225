using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Api.Configuration;

public sealed class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    /// <summary>Largest accepted request body. Sized for one image upload plus form fields.</summary>
    [Range(1024, 104857600)]
    public long MaxRequestBodyBytes { get; set; } = 6 * 1024 * 1024;

    [Range(1, 300)]
    public int RequestHeadersTimeoutSeconds { get; set; } = 30;

    [Range(1024, 1048576)]
    public int MaxRequestHeadersTotalSizeBytes { get; set; } = 32 * 1024;

    /// <summary>Slow-loris guard: minimum sustained upload rate.</summary>
    [Range(1, 100000)]
    public int MinRequestBodyBytesPerSecond { get; set; } = 240;

    /// <summary>Hard ceiling for handling one request.</summary>
    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; set; } = 60;

    [Range(4, 128)]
    public int JsonMaxDepth { get; set; } = 32;
}
