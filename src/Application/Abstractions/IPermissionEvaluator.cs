namespace NexaVerify.Application.Abstractions;

/// <summary>
/// Asks "does the caller of this request hold this permission key?" from inside an application service, for decisions that depend on
/// data (for example the submit permission of the request's own type, or whether credit figures may be shown). The API implements it
/// with the same policy the <c>[HasPermission]</c> attribute uses; outside an HTTP request the answer is always false.
/// </summary>
public interface IPermissionEvaluator
{
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken);
}
