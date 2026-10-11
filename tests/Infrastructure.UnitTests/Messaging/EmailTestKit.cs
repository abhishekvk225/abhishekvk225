using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Messaging;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Messaging;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

internal sealed class StubEnvironment : IHostEnvironment
{
    public StubEnvironment(string name)
    {
        EnvironmentName = name;
    }

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; } = "NexaVerify.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal static class EmailTestKit
{
    public const string BaseUrl = "https://portal.example.com";

    public static EmailRenderer Renderer(string footer = "NexaVerify Ltd, 1 Example Street, Bengaluru", string baseUrl = BaseUrl) =>
        new(Options.Create(new EmailOptions { Footer = footer }), new EmailLinkBuilder(baseUrl));

    /// <summary>A request with every variable and link any template can ask for, so each template can be rendered.</summary>
    public static EmailRequest Sample(EmailTemplate template, string value = "Ada Lovelace", string to = "ada@example.com")
    {
        var request = new EmailRequest(template, to, value);
        foreach (var key in new[]
        {
            "name", "company", "trialCredits", "trialDays", "hours", "minutes", "when", "how", "licenseName", "remaining", "total", "percent",
            "expiresOn", "timeLeft", "amount", "packName", "credits", "validUntil", "invoiceNumber", "creditsRevoked", "email", "endpoint", "failures", "requestedBy",
        })
        {
            request.Text(key, value);
        }

        request.Block("message", value);
        foreach (var link in new[] { "verify", "login", "forgot", "action", "reset", "buy", "invoice", "order", "manage" })
        {
            request.Link(link, "some/" + link, ("email", "ada@example.com"), ("token", "T0ken_-abc"));
        }

        return request;
    }
}
