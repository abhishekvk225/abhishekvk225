using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Public;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Public;

public interface IPublicInfoService
{
    /// <summary>Active plans flagged public. The trial plan shows the configured trial credits and period.</summary>
    Task<Result<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken cancellationToken);

    PublicConfigDto GetConfig();
}

public sealed class PublicInfoService : IPublicInfoService
{
    private readonly IPlanRepository _plans;
    private readonly SignupOptions _signup;
    private readonly CaptchaOptions _captcha;

    public PublicInfoService(IPlanRepository plans, IOptions<SignupOptions> signup, IOptions<CaptchaOptions> captcha)
    {
        _plans = plans;
        _signup = signup.Value;
        _captcha = captcha.Value;
    }

    public async Task<Result<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var plans = await _plans.ListPublicAsync(cancellationToken);
        return Result<IReadOnlyList<PublicPlanDto>>.Success(plans.Select(Map).ToList());
    }

    public PublicConfigDto GetConfig() => new(
        _signup.Enabled,
        _signup.TrialCredits,
        _signup.TrialDays,
        _captcha.IsEnabled ? new CaptchaConfigDto(CaptchaOptions.Turnstile, _captcha.SiteKey) : new CaptchaConfigDto(CaptchaOptions.None, null));

    private PublicPlanDto Map(Plan p) => new(
        p.Id,
        p.Name,
        p.Description,
        p.IsTrial ? _signup.TrialCredits : p.DefaultCredits,
        p.IsTrial ? _signup.TrialDays : p.DefaultDurationDays,
        p.Highlights.ToList(),
        p.IsTrial,
        p.DisplayPrice);
}

public interface IContactService
{
    /// <summary>Stores the message and notifies the configured address. Always succeeds for a honeypot hit (the message is dropped).</summary>
    Task<Result> SubmitAsync(SubmitContactRequest request, CancellationToken cancellationToken);

    Task<Result<PagedResult<ContactRequestDto>>> ListAsync(PageRequest page, CancellationToken cancellationToken);
}

public sealed partial class ContactService : IContactService
{
    private readonly IContactRequestRepository _requests;
    private readonly IPublicThrottle _throttle;
    private readonly IRequestInfo _request;
    private readonly IEmailOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SignupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ContactService> _logger;

    public ContactService(
        IContactRequestRepository requests,
        IPublicThrottle throttle,
        IRequestInfo request,
        IEmailOutbox outbox,
        IUnitOfWork unitOfWork,
        IOptions<SignupOptions> options,
        TimeProvider time,
        ILogger<ContactService> logger)
    {
        _requests = requests;
        _throttle = throttle;
        _request = request;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<Result> SubmitAsync(SubmitContactRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.Website))
        {
            LogHoneypot();
            return Result.Success();
        }

        if (!await _throttle.TryAcquireAsync(PublicThrottlePolicy.ContactPerIp, _request.IpAddress ?? "unknown", cancellationToken))
        {
            LogRateLimited();
            return Error.TooManyRequests(ErrorCodes.RateLimited, "Too many messages. Try again later.");
        }

        ContactRequest contact;
        try
        {
            contact = ContactRequest.Create(request.Name, request.Email, request.Company, request.Message, _time.GetUtcNow().UtcDateTime);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _requests.Add(contact);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        LogReceived(contact.Id);

        if (!string.IsNullOrWhiteSpace(_options.ContactNotifyEmail))
        {
            _outbox.Enqueue(new EmailMessage(
                _options.ContactNotifyEmail,
                $"New contact request from {Plain(contact.Name)}",
                $"Name: {Plain(contact.Name)}\nEmail: {Plain(contact.Email)}\nCompany: {Plain(contact.Company ?? "-")}\n\n{contact.Message}"));
        }

        return Result.Success();
    }

    public async Task<Result<PagedResult<ContactRequestDto>>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var paging = page.Normalize();
        var (items, total) = await _requests.ListAsync(paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<ContactRequestDto>(
            items.Select(c => new ContactRequestDto(c.Id, c.Name, c.Email, c.Company, c.Message, c.CreatedAt)).ToList(), paging.Page, paging.PageSize, total);
    }

    private static string Plain(string value) => value.ReplaceLineEndings(" ").Trim();

    [LoggerMessage(EventId = 8010, Level = LogLevel.Information, Message = "Contact request {ContactRequestId} received")]
    private partial void LogReceived(Guid contactRequestId);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Warning, Message = "Contact request dropped: honeypot field was filled")]
    private partial void LogHoneypot();

    [LoggerMessage(EventId = 8012, Level = LogLevel.Warning, Message = "Contact request refused: rate limit reached")]
    private partial void LogRateLimited();
}
