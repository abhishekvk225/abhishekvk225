namespace NexaVerify.Contracts.Common;

/// <summary>Claim and header names shared by token issuing, API-key auth and request resolution.</summary>
public static class NexaClaims
{
    public const string Subject = "sub";
    public const string ClientId = "cid";
    public const string Role = "role";
    public const string ActorType = "actor";
    public const string SecurityVersion = "sv";
    public const string MustChangePassword = "mcp";
    public const string PlatformActor = "platform";
    public const string ApiKeyActor = "apikey";
    public const string Scope = "scope";
    public const string RateLimitPerMinute = "rpm";
}

public static class HttpHeaderNames
{
    public const string CorrelationId = "X-Correlation-Id";
    public const string ApiKey = "X-Api-Key";
    public const string IdempotencyKey = "Idempotency-Key";
}
