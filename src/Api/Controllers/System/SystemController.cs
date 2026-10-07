using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Http;

namespace NexaVerify.Api.Controllers.System;

[Route("api/v1/system")]
[AllowAnonymous]
public sealed class SystemController : ApiControllerBase
{
    public sealed record SystemInfo(string Name, string Version, string Environment);

    /// <summary>Public, non-sensitive service metadata (for uptime probes and client SDK sanity checks).</summary>
    [HttpGet("info")]
    [ProducesResponseType<SystemInfo>(StatusCodes.Status200OK)]
    public ActionResult<SystemInfo> GetInfo([FromServices] IHostEnvironment environment)
    {
        var version = typeof(SystemController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        return new SystemInfo("NexaVerify API", version, environment.EnvironmentName);
    }
}
