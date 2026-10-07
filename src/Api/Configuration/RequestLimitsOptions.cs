using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Api.Configuration;

public sealed class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    /// <summary>Largest accepted request body. Sized for one image upload plus form fields.</summary>
    [Range(1024, 104857600)]
    public long MaxRequestBodyBytes { get; set; } = 6 * 1024 * 1024;
}

public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    public bool RedirectToHttps { get; set; } = true;
}
