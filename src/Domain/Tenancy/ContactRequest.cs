using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Domain.Tenancy;

/// <summary>A message sent through the public "contact us" form. Belongs to no tenant; purged after the retention period.</summary>
public sealed class ContactRequest : Entity
{
    public const int NameMaxLength = 150;
    public const int CompanyMaxLength = 150;
    public const int MessageMaxLength = 4000;

    private ContactRequest()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string? Company { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public static ContactRequest Create(string name, string email, string? company, string message, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new DomainException("CONTACT_NAME_INVALID", "A name of at most 150 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > User.EmailMaxLength)
        {
            throw new DomainException("CONTACT_EMAIL_INVALID", "A valid email address is required.");
        }

        if (string.IsNullOrWhiteSpace(message) || message.Trim().Length > MessageMaxLength)
        {
            throw new DomainException("CONTACT_MESSAGE_INVALID", "A message of at most 4000 characters is required.");
        }

        if (company is { Length: > 0 } && company.Trim().Length > CompanyMaxLength)
        {
            throw new DomainException("CONTACT_COMPANY_INVALID", "The company name is too long.");
        }

        return new ContactRequest
        {
            Name = name.Trim(),
            Email = email.Trim(),
            Company = string.IsNullOrWhiteSpace(company) ? null : company.Trim(),
            Message = message.Trim(),
            CreatedAt = now,
        };
    }
}
