using NexaVerify.Domain.Messaging;

namespace NexaVerify.Infrastructure.Messaging;

/// <summary>A piece of an email body. The same blocks produce the HTML and the plain-text version, so the two cannot drift apart.</summary>
internal abstract record EmailBlock;

/// <summary>A paragraph; <c>{variable}</c> placeholders are replaced with the (encoded) caller values.</summary>
internal sealed record Para(string Text) : EmailBlock;

/// <summary>A call to action: a button in HTML, "label:" plus the URL on a line of its own in text.</summary>
internal sealed record Button(string Label, string LinkKey, bool Primary = true, bool Optional = false) : EmailBlock;

/// <summary>Label/value rows (an invoice number, a contact's details).</summary>
internal sealed record Facts(IReadOnlyList<(string Label, string Value)> Rows) : EmailBlock;

/// <summary>A multi-line variable shown as a quotation (a visitor's message).</summary>
internal sealed record Quote(string Variable) : EmailBlock;

/// <summary>Small print under the main content.</summary>
internal sealed record Fine(string Text) : EmailBlock;

/// <summary>What one template says. Wording lives here and only here.</summary>
internal sealed record EmailContent(
    string Subject, string Heading, string Preheader, string Reason, bool OneTimeToken, IReadOnlyList<EmailBlock> Blocks);

internal static class EmailTemplateCatalog
{
    private const string Account = "You are receiving this email because of activity on your NexaVerify account.";

    private static readonly IReadOnlyDictionary<EmailTemplate, EmailContent> Contents = new Dictionary<EmailTemplate, EmailContent>
    {
        [EmailTemplate.VerifyEmail] = new(
            "Confirm your NexaVerify account", "Confirm your email address", "One click to create your account and start your free trial.",
            "You are receiving this email because this address was used to sign up for NexaVerify.", true,
            [
                new Para("Hello {name},"),
                new Para("Thank you for signing up {company} for NexaVerify. Confirm your email address to create your account and start your free trial ({trialCredits} credits for {trialDays} days). The link expires in {hours} hours and can be used once."),
                new Button("Confirm my email address", "verify"),
                new Fine("If you did not sign up, you can ignore this email."),
            ]),

        [EmailTemplate.AlreadyRegistered] = new(
            "You already have a NexaVerify account", "You already have an account", "Someone tried to sign up with this address.",
            "You are receiving this email because this address was used to sign up for NexaVerify.", false,
            [
                new Para("Hello {name},"),
                new Para("Someone used this email address to sign up for NexaVerify, but it already belongs to an account. You can sign in here:"),
                new Button("Sign in", "login"),
                new Para("If you forgot your password you can reset it here:"),
                new Button("Reset my password", "forgot", Primary: false),
                new Fine("If this was not you, you can ignore this email."),
            ]),

        [EmailTemplate.PasswordReset] = new(
            "Reset your NexaVerify password", "Choose a new password", "Use the link to set a new password.",
            "You are receiving this email because a password reset was requested for your NexaVerify account.", true,
            [
                new Para("Hello {name},"),
                new Para("Use the link below to choose a new password. It expires in {minutes} minutes and can be used once."),
                new Button("Choose a new password", "action"),
                new Fine("If you did not request this, you can ignore this email."),
            ]),

        [EmailTemplate.UserInvite] = new(
            "You have been invited to NexaVerify", "You have been invited", "Choose your password to start using NexaVerify.",
            "You are receiving this email because an account was created for you on NexaVerify.", true,
            [
                new Para("Hello {name},"),
                new Para("An account has been created for you. Use the link below to choose your password. It expires in {hours} hours and can be used once."),
                new Button("Choose my password", "action"),
                new Fine("If you were not expecting this invitation, you can ignore this email."),
            ]),

        [EmailTemplate.PasswordChanged] = new(
            "Your NexaVerify password was changed", "Your password was changed", "Your NexaVerify password was changed.",
            Account, false,
            [
                new Para("Hello {name},"),
                new Para("The password of your NexaVerify account was changed on {when} ({how}). Your other sessions were signed out."),
                new Para("If this was you, no action is needed. If it was not, reset your password now."),
                new Button("Reset my password", "reset"),
            ]),

        [EmailTemplate.MfaEnabled] = new(
            "Two-factor authentication was turned on", "Two-factor authentication is on", "Sign-ins now ask for a second step.",
            Account, false,
            [
                new Para("Hello {name},"),
                new Para("Two-factor authentication was turned on for your NexaVerify account on {when}. From now on, signing in asks for a code from your authenticator app. Your other sessions were signed out."),
                new Para("If this was not you, reset your password now."),
                new Button("Reset my password", "reset"),
            ]),

        [EmailTemplate.MfaDisabled] = new(
            "Two-factor authentication was turned off", "Two-factor authentication was turned off", "An administrator reset your second step.",
            Account, false,
            [
                new Para("Hello {name},"),
                new Para("An administrator reset the two-factor authentication of your NexaVerify account on {when}. Your account no longer asks for a second step until you set it up again, so please sign in and do that soon."),
                new Button("Sign in", "login"),
                new Fine("If you did not expect this, contact your administrator."),
            ]),

        [EmailTemplate.MfaRecoveryCodeUsed] = new(
            "A recovery code was used to sign in", "A recovery code was used", "A one-time recovery code signed in to your account.",
            Account, false,
            [
                new Para("Hello {name},"),
                new Para("A one-time recovery code was used to sign in to your NexaVerify account on {when}. Each code works only once; consider generating a fresh set in your security settings."),
                new Para("If this was not you, reset your password now and set up two-factor authentication again."),
                new Button("Reset my password", "reset"),
            ]),

        [EmailTemplate.LicenseLowBalance] = new(
            "Your NexaVerify credits are running low", "Your credits are running low", "{remaining} of {total} credits left.",
            "You are receiving this email because you are the contact for this NexaVerify account.", false,
            [
                new Para("Hello {company},"),
                new Para("License \"{licenseName}\" has {remaining} of {total} credits left ({percent}%). When the credits run out, requests that use them stop until you add more."),
                new Button("Buy more credits", "buy", Optional: true),
            ]),

        [EmailTemplate.LicenseExpiring] = new(
            "Your NexaVerify license expires soon", "Your license expires soon", "License \"{licenseName}\" ends on {expiresOn}.",
            "You are receiving this email because you are the contact for this NexaVerify account.", false,
            [
                new Para("Hello {company},"),
                new Para("License \"{licenseName}\" expires on {expiresOn} ({timeLeft} left) with {remaining} credits remaining. Credits that are not used by then are lost."),
                new Button("Buy more credits", "buy", Optional: true),
            ]),

        [EmailTemplate.PaymentReceipt] = new(
            "Payment received - invoice {invoiceNumber}", "Payment received", "Thank you. Your credits were added.",
            "You are receiving this email because a payment was made for your NexaVerify account.", false,
            [
                new Para("Hello,"),
                new Para("We received your payment of {amount} for \"{packName}\". {credits} credits were added to your account and stay valid until {validUntil} (UTC)."),
                new Facts([("Invoice", "{invoiceNumber}"), ("Amount", "{amount}")]),
                new Button("View the order and print the invoice", "invoice"),
                new Fine("Thank you."),
            ]),

        [EmailTemplate.RefundNotice] = new(
            "Refund issued - {invoiceNumber}", "A refund was issued", "{amount} was refunded.",
            "You are receiving this email because a refund was issued for your NexaVerify account.", false,
            [
                new Para("Hello,"),
                new Para("We refunded {amount} of your purchase \"{packName}\" ({invoiceNumber}). {creditsRevoked} unused credits were taken back from your account."),
                new Button("View the order", "order"),
            ]),

        [EmailTemplate.ContactRequest] = new(
            "New contact request from {name}", "New contact request", "{name} wrote through the contact form.",
            "You are receiving this email because you are the notification contact for the NexaVerify contact form.", false,
            [
                new Facts([("Name", "{name}"), ("Email", "{email}"), ("Company", "{company}")]),
                new Quote("message"),
            ]),

        [EmailTemplate.WebhookEndpointDisabled] = new(
            "A NexaVerify webhook endpoint was disabled", "A webhook endpoint was disabled", "We stopped sending events to a failing endpoint.",
            "You are receiving this email because you are the contact for this NexaVerify account.", false,
            [
                new Para("Hello {company},"),
                new Para("We disabled the webhook endpoint {endpoint} after {failures} failed events in a row. No further events are sent to it until you fix the problem and enable it again."),
                new Button("Review my webhooks", "manage", Optional: true),
            ]),

        [EmailTemplate.AdminTest] = new(
            "NexaVerify test email", "Test email", "Outgoing email works.",
            "You are receiving this email because an administrator asked NexaVerify to send a test message to this address.", false,
            [
                new Para("This is a test message from NexaVerify, requested by administrator {requestedBy}."),
                new Para("If you can read this, outgoing email is configured correctly."),
            ]),
    };

    public static EmailContent For(EmailTemplate template) =>
        Contents.TryGetValue(template, out var content) ? content : throw new InvalidOperationException($"No email content is defined for template {template}.");

    public static bool HasOneTimeToken(EmailTemplate template) => For(template).OneTimeToken;
}
