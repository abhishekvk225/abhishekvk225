using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Application.UnitTests;

public class ResultTests
{
    [Fact]
    public void Success_exposes_value_and_no_error()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void Failure_has_error_and_throws_on_value_access()
    {
        Result<int> result = Error.NotFound();

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.NotFound);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Map_transforms_success_and_propagates_failure()
    {
        Result<int>.Success(2).Map(x => x * 3).Value.ShouldBe(6);

        var error = Error.PaymentRequired(ErrorCodes.LicenseExpired, "expired");
        var mapped = Result<int>.Failure(error).Map(x => x.ToString());
        mapped.IsFailure.ShouldBeTrue();
        mapped.Error.ShouldBe(error);
    }

    [Fact]
    public void Non_generic_result_converts_from_error()
    {
        Result ok = Result.Success();
        Result failed = Error.Forbidden(ErrorCodes.ClientSuspended, "suspended");

        ok.IsSuccess.ShouldBeTrue();
        failed.IsFailure.ShouldBeTrue();
        failed.Error!.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void Failure_rejects_null_error()
    {
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result<int>.Failure(null!));
    }

    [Fact]
    public void Validation_error_carries_field_errors()
    {
        var error = Error.Validation("bad", new Dictionary<string, string[]> { ["email"] = ["required"] });

        error.Code.ShouldBe(ErrorCodes.ValidationFailed);
        error.FieldErrors!["email"].ShouldBe(["required"]);
    }
}
