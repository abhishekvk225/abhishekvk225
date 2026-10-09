using FluentValidation;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Billing;
using NexaVerify.Domain.Billing;

namespace NexaVerify.Application.Billing;

public sealed class CheckoutRequestValidator : AbstractValidator<CheckoutRequest>
{
    public CheckoutRequestValidator()
    {
        RuleFor(x => x.PackId).NotEmpty();
        RuleFor(x => x.IdempotencyKey).MaximumLength(PaymentOrder.IdempotencyKeyMaxLength)
            .Matches("^[A-Za-z0-9._:-]*$").WithMessage("Use letters, digits and . _ : - only.");
    }
}

public sealed class UpdateBillingProfileRequestValidator : AbstractValidator<UpdateBillingProfileRequest>
{
    public UpdateBillingProfileRequestValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(BillingProfile.NameMaxLength);
        RuleFor(x => x.AddressLine1).NotEmpty().MaximumLength(BillingProfile.LineMaxLength);
        RuleFor(x => x.AddressLine2).MaximumLength(BillingProfile.LineMaxLength);
        RuleFor(x => x.City).NotEmpty().MaximumLength(BillingProfile.ShortMaxLength);
        RuleFor(x => x.State).MaximumLength(BillingProfile.ShortMaxLength);
        RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(BillingProfile.PostalMaxLength);
        RuleFor(x => x.Country).NotEmpty().Must(c => c.Trim().Length == 2 && c.Trim().All(char.IsAsciiLetter)).WithMessage("Country must be a 2-letter ISO code.");
        RuleFor(x => x.TaxId).MaximumLength(BillingProfile.TaxIdMaxLength + 8)
            .Must(t => string.IsNullOrWhiteSpace(t) || t.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-')).WithMessage("The tax id may contain letters and digits only.");
        RuleFor(x => x.BillingEmail).Email();
    }
}

public sealed class CreateCreditPackRequestValidator : AbstractValidator<CreateCreditPackRequest>
{
    public CreateCreditPackRequestValidator()
    {
        PackRules.Apply(this, x => x.Name, x => x.Description, x => x.Credits, x => x.ValidityDays, x => x.PriceMinor, x => x.Currency, x => x.Highlights, x => x.DisplayOrder);
    }
}

public sealed class UpdateCreditPackRequestValidator : AbstractValidator<UpdateCreditPackRequest>
{
    public UpdateCreditPackRequestValidator()
    {
        PackRules.Apply(this, x => x.Name, x => x.Description, x => x.Credits, x => x.ValidityDays, x => x.PriceMinor, x => x.Currency, x => x.Highlights, x => x.DisplayOrder);
    }
}

internal static class PackRules
{
    public static void Apply<T>(
        AbstractValidator<T> v,
        System.Linq.Expressions.Expression<Func<T, string>> name,
        System.Linq.Expressions.Expression<Func<T, string?>> description,
        System.Linq.Expressions.Expression<Func<T, int>> credits,
        System.Linq.Expressions.Expression<Func<T, int>> validityDays,
        System.Linq.Expressions.Expression<Func<T, long>> price,
        System.Linq.Expressions.Expression<Func<T, string>> currency,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<string>?>> highlights,
        System.Linq.Expressions.Expression<Func<T, int>> displayOrder)
    {
        v.RuleFor(name).NotEmpty().MaximumLength(CreditPack.NameMaxLength);
        v.RuleFor(description).MaximumLength(CreditPack.DescriptionMaxLength);
        v.RuleFor(credits).InclusiveBetween(1, CreditPack.MaxCredits);
        v.RuleFor(validityDays).InclusiveBetween(1, CreditPack.MaxValidityDays);
        v.RuleFor(price).InclusiveBetween(1, CreditPack.MaxPriceMinor);
        v.RuleFor(currency).NotEmpty().Must(c => CreditPack.IsCurrencyCode((c ?? string.Empty).Trim().ToUpperInvariant())).WithMessage("Use a 3-letter ISO 4217 code such as INR or USD.");
        v.RuleFor(highlights).Must(h => h is null || (h.Count <= CreditPack.MaxHighlights && h.All(x => (x ?? string.Empty).Trim().Length <= CreditPack.HighlightMaxLength)))
            .WithMessage("At most 8 highlights of 120 characters each.");
        v.RuleFor(displayOrder).InclusiveBetween(-10_000, 10_000);
    }
}

public sealed class RefundOrderRequestValidator : AbstractValidator<RefundOrderRequest>
{
    public RefundOrderRequestValidator()
    {
        RuleFor(x => x.AmountMinor).GreaterThan(0).When(x => x.AmountMinor.HasValue);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(Refund.ReasonMaxLength);
    }
}

public sealed class SimulatePaymentRequestValidator : AbstractValidator<SimulatePaymentRequest>
{
    public SimulatePaymentRequestValidator()
    {
        RuleFor(x => x.Outcome).NotEmpty().MaximumLength(20);
    }
}
