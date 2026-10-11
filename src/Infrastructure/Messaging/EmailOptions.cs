using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace NexaVerify.Infrastructure.Messaging;

public enum SmtpSecurity
{
    /// <summary>Connect in clear text and REQUIRE an upgrade with STARTTLS (port 587/25). The connection is dropped if the server does not offer it.</summary>
    StartTls = 0,

    /// <summary>TLS from the first byte (implicit TLS, "SMTPS", port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption. Only accepted for a loopback server, or when <c>Email:Smtp:AllowPlaintext</c> is set on purpose (a trusted private relay).</summary>
    None,
}

public sealed class EmailSenderIdentity
{
    [MaxLength(100)]
    public string Name { get; set; } = "NexaVerify";

    public string Address { get; set; } = "no-reply@nexaverify.invalid";
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? Username { get; set; }

    /// <summary>SECRET. Environment variable or the secrets directory only (<c>Email__Smtp__Password</c>), never a settings file.</summary>
    public string? Password { get; set; }

    /// <summary>Connect, TLS handshake, every SMTP command and the data transfer each give up after this long; the whole send after four times it.</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Name announced in EHLO. Empty = the machine name.</summary>
    public string? LocalDomain { get; set; }

    /// <summary>
    /// Optional PEM file with the certificate authority that signed the mail server's certificate (a private relay with an internal CA).
    /// The chain is still validated up to that authority, and the host name still has to match: this adds a trusted root, it never switches validation off.
    /// </summary>
    public string? CaCertificateFile { get; set; }

    /// <summary>Permits <see cref="SmtpSecurity.None"/> towards a non-loopback host (a private relay on a trusted network). Credentials would travel in clear text.</summary>
    public bool AllowPlaintext { get; set; }
}

public sealed class EmailDispatcherOptions
{
    /// <summary>Turns the delivery loop off on this node (tests drive the dispatcher by hand; a web-only node can leave delivery to others).</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;

    /// <summary>Most messages one cycle claims.</summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Messages sent at the same time by one node (each over its own SMTP connection).</summary>
    [Range(1, 32)]
    public int Parallelism { get; set; } = 4;

    /// <summary>Attempts before a message that keeps failing transiently is abandoned.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 6;

    /// <summary>First retry delay; each further attempt doubles it (exponential backoff) up to <see cref="MaxDelaySeconds"/>.</summary>
    [Range(1, 3600)]
    public int BaseDelaySeconds { get; set; } = 30;

    [Range(1, 86400)]
    public int MaxDelaySeconds { get; set; } = 3600;

    /// <summary>Each delay is randomised by up to this many percent either way, so retries of a mail-server outage do not arrive in one burst.</summary>
    [Range(0, 50)]
    public int JitterPercent { get; set; } = 20;

    /// <summary>Fairness/throttle: one recipient domain gets at most this many messages in a cycle (per node), so a big mailing to one provider cannot starve the rest or trip its limits.</summary>
    [Range(1, 500)]
    public int MaxPerDomainPerCycle { get; set; } = 5;

    /// <summary>
    /// How long a claimed message is reserved for this node before another may take it. 0 = derive it from the batch, parallelism and SMTP
    /// timeout (the worst case of a cycle). A smaller explicit value is refused at startup: a message still in flight could be sent twice.
    /// </summary>
    [Range(0, 7200)]
    public int LeaseSeconds { get; set; }

    /// <summary>Finished messages (and their rendered bodies) are deleted after this many days.</summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 30;
}

/// <summary>Everything about outgoing email (docs: deploy/CONFIG.md "Email").</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public const string ProviderLogging = "Logging";
    public const string ProviderSmtp = "Smtp";

    /// <summary><c>Logging</c> (writes the mail to the log; Development/Testing) or <c>Smtp</c>.</summary>
    public string Provider { get; set; } = ProviderLogging;

    /// <summary>Allows the Logging provider outside Development/Testing (a demo deployment that must not send mail). Everywhere else the application refuses to start with it.</summary>
    public bool AllowLoggingProvider { get; set; }

    /// <summary>Development only (Logging provider): also log the message body, which contains one-time links. Never enable in production.</summary>
    public bool LogBodies { get; set; }

    public EmailSenderIdentity From { get; set; } = new();

    /// <summary>Address replies go to (optional).</summary>
    public string? ReplyTo { get; set; }

    /// <summary>Every link in a mail is built under this address. Empty = <c>Portal:PublicBaseUrl</c>.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Legal / postal address text shown in the footer of every mail (may be several lines).</summary>
    [MaxLength(500)]
    public string Footer { get; set; } = string.Empty;

    /// <summary>Platform-wide ceiling on delivery attempts per minute (all nodes together, counted in the database).</summary>
    [Range(1, 100000)]
    public int MaxPerMinute { get; set; } = 120;

    public SmtpOptions Smtp { get; set; } = new();

    public EmailDispatcherOptions Dispatcher { get; set; } = new();

    public bool IsSmtp => string.Equals(Provider, ProviderSmtp, StringComparison.OrdinalIgnoreCase);
}

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    private readonly IHostEnvironment _environment;

    public EmailOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public static bool IsDevelopmentLike(IHostEnvironment environment) =>
        environment.IsDevelopment() || string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    /// <summary>The address as MimeKit would deliver it, or null when it is not a plain mailbox (display names, groups, control characters and the like are refused).</summary>
    public static string? PlainAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return null;
        }

        return MailboxAddress.TryParse(ParserOptions.Default, value, out var mailbox)
            && string.Equals(mailbox.Address, value, StringComparison.Ordinal)
            && mailbox.Address.IndexOf('@', StringComparison.Ordinal) is > 0 and var at
            && mailbox.Address.IndexOf('.', at) > at + 1
                ? mailbox.Address
                : null;
    }

    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var errors = new List<string>();
        var smtp = options.IsSmtp;
        if (!smtp && !string.Equals(options.Provider, EmailOptions.ProviderLogging, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Email:Provider must be 'Logging' or 'Smtp'.");
        }

        if (!smtp && !IsDevelopmentLike(_environment) && !options.AllowLoggingProvider)
        {
            errors.Add($"The Logging email provider only logs mail, so sign-up links and password resets would never reach anyone. It is refused in the '{_environment.EnvironmentName}' environment: set Email:Provider to 'Smtp' (see deploy/CONFIG.md), or set Email:AllowLoggingProvider=true for a deployment that must not send mail.");
        }

        if (PlainAddress(options.From.Address) is null)
        {
            errors.Add("Email:From:Address must be a plain email address.");
        }

        if (options.From.Name.Any(char.IsControl))
        {
            errors.Add("Email:From:Name must not contain control characters.");
        }

        if (!string.IsNullOrWhiteSpace(options.ReplyTo) && PlainAddress(options.ReplyTo) is null)
        {
            errors.Add("Email:ReplyTo must be a plain email address.");
        }

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) && !EmailLinkBuilder.IsValidBase(options.PublicBaseUrl))
        {
            errors.Add("Email:PublicBaseUrl must be an absolute https URL without query or fragment (http only for localhost).");
        }

        if (smtp)
        {
            var s = options.Smtp;
            if (string.IsNullOrWhiteSpace(s.Host) || s.Host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                errors.Add("Email:Smtp:Host is required when Email:Provider is 'Smtp'.");
            }

            if (!Enum.IsDefined(s.Security))
            {
                errors.Add("Email:Smtp:Security must be StartTls, SslOnConnect or None.");
            }
            else if (s.Security == SmtpSecurity.None && !s.AllowPlaintext && !IsLoopback(s.Host))
            {
                errors.Add("Email:Smtp:Security=None sends mail (and the password) unencrypted. Use StartTls or SslOnConnect, or set Email:Smtp:AllowPlaintext=true for a trusted private relay.");
            }

            if (!string.IsNullOrWhiteSpace(s.CaCertificateFile) && !File.Exists(s.CaCertificateFile))
            {
                errors.Add("Email:Smtp:CaCertificateFile does not point to an existing file.");
            }

            if (string.IsNullOrEmpty(s.Username) != string.IsNullOrEmpty(s.Password))
            {
                errors.Add("Email:Smtp:Username and Email:Smtp:Password must be set together.");
            }
        }

        var d = options.Dispatcher;
        if (d.MaxDelaySeconds < d.BaseDelaySeconds)
        {
            errors.Add("Email:Dispatcher:MaxDelaySeconds must not be smaller than BaseDelaySeconds.");
        }

        if (d.LeaseSeconds != 0 && d.LeaseSeconds < DerivedLease(options).TotalSeconds)
        {
            errors.Add($"Email:Dispatcher:LeaseSeconds ({d.LeaseSeconds}) is shorter than the worst case of one dispatch cycle ({(int)Math.Ceiling(DerivedLease(options).TotalSeconds)} s). Raise it, or leave it 0 to derive it.");
        }

        errors.AddRange(Annotations(options.From));
        errors.AddRange(Annotations(options.Smtp));
        errors.AddRange(Annotations(options.Dispatcher));
        errors.AddRange(Annotations(options));
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>The lease the worst-case cycle needs: every slot waits for its full SMTP budget, plus a margin.</summary>
    public static TimeSpan DerivedLease(EmailOptions options) =>
        TimeSpan.FromSeconds((Math.Ceiling(options.Dispatcher.BatchSize / (double)options.Dispatcher.Parallelism) * ((options.Smtp.TimeoutSeconds * 4) + 5)) + 30);

    public static TimeSpan ResolveLease(EmailOptions options) =>
        options.Dispatcher.LeaseSeconds == 0 ? DerivedLease(options) : TimeSpan.FromSeconds(options.Dispatcher.LeaseSeconds);

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    private static IEnumerable<string> Annotations(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results.Select(r => $"Email: {r.ErrorMessage}");
    }
}
