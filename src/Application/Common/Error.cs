namespace NexaVerify.Application.Common;

/// <summary>Category of failure; the API layer maps this to an HTTP status in exactly one place.</summary>
public enum ErrorType
{
    Failure = 0,
    Validation,
    NotFound,
    Conflict,
    Unauthenticated,
    Forbidden,
    PaymentRequired,
    TooManyRequests,
    PayloadTooLarge,
    UnprocessableEntity,
    Unavailable,
}

/// <summary>A business failure: stable <see cref="Code"/> (from ErrorCodes), human message, optional field errors.</summary>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
    public static Error Validation(string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(Contracts.Common.ErrorCodes.ValidationFailed, message, ErrorType.Validation, fieldErrors);

    public static Error NotFound(string message = "The requested resource was not found.") =>
        new(Contracts.Common.ErrorCodes.NotFound, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Unauthenticated(string code, string message) => new(code, message, ErrorType.Unauthenticated);

    public static Error PaymentRequired(string code, string message) => new(code, message, ErrorType.PaymentRequired);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    public static Error TooManyRequests(string code, string message) => new(code, message, ErrorType.TooManyRequests);

    public static Error PayloadTooLarge(string message) => new(Contracts.Common.ErrorCodes.PayloadTooLarge, message, ErrorType.PayloadTooLarge);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.UnprocessableEntity);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);
}
