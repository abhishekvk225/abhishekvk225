using System.Text.Json;
using FluentValidation;
using NexaVerify.Contracts.Faces;

namespace NexaVerify.Application.Faces;

public sealed class EnrollFaceRequestValidator : AbstractValidator<EnrollFaceRequest>
{
    public EnrollFaceRequestValidator()
    {
        RuleFor(x => x.ExternalRef).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ConsentReference).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).MaximumLength(200);
        RuleFor(x => x.Metadata).Must(MetadataRules.IsValid).WithMessage(MetadataRules.Message);
    }
}

public sealed class VerifyFaceRequestValidator : AbstractValidator<VerifyFaceRequest>
{
    public VerifyFaceRequestValidator()
    {
        RuleFor(x => x).Must(x => x.ProfileId.HasValue ^ !string.IsNullOrWhiteSpace(x.ExternalRef))
            .WithMessage("Provide exactly one of profileId or externalRef.");
        RuleFor(x => x.ExternalRef).MaximumLength(100);
    }
}

public sealed class IdentifyFaceRequestValidator : AbstractValidator<IdentifyFaceRequest>
{
    public IdentifyFaceRequestValidator()
    {
        RuleFor(x => x.TopK).InclusiveBetween(1, 20).When(x => x.TopK.HasValue);
    }
}

public sealed class UpdateFaceProfileRequestValidator : AbstractValidator<UpdateFaceProfileRequest>
{
    public UpdateFaceProfileRequestValidator()
    {
        RuleFor(x => x.DisplayName).MaximumLength(200);
        RuleFor(x => x.Metadata).Must(MetadataRules.IsValid).WithMessage(MetadataRules.Message);
        RuleFor(x => x.Status).Must(s => s is "Active" or "Disabled").When(x => x.Status is not null).WithMessage("Status must be Active or Disabled.");
    }
}

/// <summary>Client-defined metadata: a flat JSON object of short scalar values, at most 4 KB.</summary>
internal static class MetadataRules
{
    public const string Message = "Metadata must be a flat JSON object of text, number or boolean values (at most 4 KB, 50 keys).";

    public static bool IsValid(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        if (json.Length > 4096)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var count = 0;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (++count > 50 || property.Name.Length > 100
                    || property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
