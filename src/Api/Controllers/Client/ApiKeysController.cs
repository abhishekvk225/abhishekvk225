using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Client;

/// <summary>A client manages its own API keys. The raw key appears once, in the response that creates (or regenerates) it.</summary>
[Route("api/v1/client/api-keys")]
public sealed class ApiKeysController : ApiControllerBase
{
    private readonly IApiKeyService _keys;

    public ApiKeysController(IApiKeyService keys)
    {
        _keys = keys;
    }

    [HttpGet]
    [HasPermission(Permissions.ApiKeys.Read)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => ToActionResult(await _keys.ListAsync(cancellationToken));

    [HttpGet("scopes")]
    [HasPermission(Permissions.ApiKeys.Read)]
    public IActionResult Scopes() => Ok(_keys.Scopes());

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.ApiKeys.Read)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => ToActionResult(await _keys.GetAsync(id, cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.ApiKeys.Manage)]
    public async Task<IActionResult> Create(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        var result = await _keys.CreateAsync(request, cancellationToken);
        return ToActionResult(result, created => CreatedAtAction(nameof(Get), new { id = created.Key.Id }, created));
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.ApiKeys.Manage)]
    public async Task<IActionResult> Update(Guid id, UpdateApiKeyRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _keys.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/revoke")]
    [HasPermission(Permissions.ApiKeys.Manage)]
    public async Task<IActionResult> Revoke(Guid id, RevokeApiKeyRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _keys.RevokeAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/regenerate")]
    [HasPermission(Permissions.ApiKeys.Manage)]
    public async Task<IActionResult> Regenerate(Guid id, RegenerateApiKeyRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _keys.RegenerateAsync(id, request, cancellationToken));
}

/// <summary>The client's own API call history (who called what, with what result). No request content is ever stored.</summary>
[Route("api/v1/client/api-logs")]
public sealed class ApiLogsController : ApiControllerBase
{
    private readonly IApiLogService _logs;

    public ApiLogsController(IApiLogService logs)
    {
        _logs = logs;
    }

    [HttpGet]
    [HasPermission(Permissions.ApiKeys.Read)]
    public async Task<IActionResult> List([FromQuery] ApiLogQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _logs.ListAsync(query, cancellationToken));
}
