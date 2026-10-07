namespace NexaVerify.Web.Services;

/// <summary>Problem details as the UI needs them (mirrors the API's RFC 7807 payload, docs/03 §1).</summary>
public sealed record ApiError(
    string Code,
    string Message,
    string? CorrelationId = null,
    int? Status = null,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
    public static ApiError Unexpected(string? correlationId = null) =>
        new("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", correlationId, 500);
}

/// <summary>
/// Result of every typed API client call. Pages never see exceptions or raw HTTP: they get a value or an <see cref="ApiError"/>.
/// </summary>
public sealed class ApiResult<T>
{
    private readonly T? _value;

    private ApiResult(T? value, ApiError? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public ApiError? Error { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Cannot read Value of a failed ApiResult.");

    public static ApiResult<T> Ok(T value) => new(value, null);

    public static ApiResult<T> Fail(ApiError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    public static ApiResult<T> Fail(string code, string message, string? correlationId = null, int? status = null) =>
        Fail(new ApiError(code, message, correlationId, status));
}
