using NexaVerify.Domain.Common;

namespace NexaVerify.Application.Common;

public static class DomainErrors
{
    /// <summary>Maps a domain rule violation to the API error model: malformed input → 400, state conflicts → 409.</summary>
    public static Error ToError(this DomainException ex) =>
        ex.Code.EndsWith("_INVALID", StringComparison.Ordinal) || ex.Code.EndsWith("_REQUIRED", StringComparison.Ordinal)
            ? Error.Validation(ex.Message)
            : Error.Conflict(ex.Code, ex.Message);
}
