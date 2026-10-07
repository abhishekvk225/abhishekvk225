using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Tenancy;

internal static class ClientMapping
{
    public static ClientDto ToDto(this Client c) => new(
        c.Id, c.Code, c.Name, c.LegalName, c.ContactEmail, c.ContactPhone, c.AddressLine1, c.AddressLine2, c.City, c.State,
        c.PostalCode, c.Country, c.Website, c.Industry, c.TimeZone, c.Status.ToString(), c.StatusReason, c.StatusChangedAt,
        c.Notes, c.CreatedAt, c.UpdatedAt, Convert.ToBase64String(c.RowVersion));

    public static ClientListItemDto ToListItem(this ClientListRow row) => new(
        row.Client.Id, row.Client.Code, row.Client.Name, row.Client.ContactEmail, row.Client.Status.ToString(),
        row.Client.Country, row.Client.CreatedAt, row.UserCount);

    public static ClientUserDto ToDto(this ClientUserRow row) => new(
        row.User.Id, row.User.Email, row.User.FullName, row.User.Status.ToString(), row.Role, row.Membership?.JobTitle,
        row.Membership?.IsOwner ?? false, row.User.LastLoginAt, row.User.MustChangePassword, row.User.CreatedAt);

    public static AuditLogDto ToDto(this Domain.Auditing.AuditLog a) => new(
        a.Id, a.OccurredAt, a.Action, a.EntityType, a.EntityId, a.ActorType.ToString(), a.ActorId, a.IpAddress, a.CorrelationId);

    public static LoginHistoryDto ToDto(this Domain.Identity.LoginHistory h) => new(
        h.Id, h.OccurredAt, h.Outcome.ToString(), h.EmailAttempted, h.UserId, h.IpAddress, h.UserAgent, h.FailureReason);

    public static void Apply(this Client c, string name, string? legalName, string contactEmail, string? phone, string? line1, string? line2,
        string? city, string? state, string? postal, string? country, string? website, string? industry, string timeZone)
    {
        c.Name = name.Trim();
        c.LegalName = Clean(legalName);
        c.ContactEmail = contactEmail.Trim();
        c.ContactPhone = Clean(phone);
        c.AddressLine1 = Clean(line1);
        c.AddressLine2 = Clean(line2);
        c.City = Clean(city);
        c.State = Clean(state);
        c.PostalCode = Clean(postal);
        c.Country = Clean(country)?.ToUpperInvariant();
        c.Website = Clean(website);
        c.Industry = Clean(industry);
        c.TimeZone = timeZone;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
