using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Http;

namespace NexaVerify.Api.Controllers.System;

[Route("api/v1/system")]
[AllowAnonymous]
public sealed class SystemController : ApiControllerBase
{
    public sealed record SystemInfo(string Name, string Status);

    /// <summary>Public liveness metadata. Deliberately reveals neither version nor environment.</summary>
    [HttpGet("info")]
    [ProducesResponseType<SystemInfo>(StatusCodes.Status200OK)]
    public ActionResult<SystemInfo> GetInfo() => new SystemInfo("NexaVerify API", "ok");
}
