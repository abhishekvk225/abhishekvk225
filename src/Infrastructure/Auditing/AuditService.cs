using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Common;

namespace NexaVerify.Infrastructure.Auditing;

/// <summary>Stages audit rows in the current unit of work, so the audit trail commits atomically with the change it describes.</summary>
public sealed class AuditService : IAuditService
{
    private static readonly string[] SensitiveFragments = ["password", "secret", "token", "hash", "embedding", "apikey", "privatekey"];

    private static readonly JsonSerializerOptions Json = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                typeInfo =>
                {
                    foreach (var property in typeInfo.Properties.ToList())
                    {
                        var member = property.AttributeProvider as MemberInfo;
                        if (member?.GetCustomAttribute<AuditIgnoreAttribute>() is not null
                            || SensitiveFragments.Any(f => property.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                        {
                            typeInfo.Properties.Remove(property);
                        }
                    }
                },
            },
        },
        MaxDepth = 8,
    };

    private readonly IAuditLogWriter _writer;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenant;
    private readonly IRequestInfo _request;
    private readonly TimeProvider _time;

    public AuditService(IAuditLogWriter writer, ICurrentUser currentUser, ITenantContext tenant, IRequestInfo request, TimeProvider time)
    {
        _writer = writer;
        _currentUser = currentUser;
        _tenant = tenant;
        _request = request;
        _time = time;
    }

    public void Record(AuditEntry entry)
    {
        var actorType = entry.ActorId is not null || _currentUser.ActorType == ActorType.User
            ? AuditActorType.User
            : _currentUser.ActorType == ActorType.ApiKey ? AuditActorType.ApiKey : AuditActorType.System;

        _writer.Add(new AuditLog
        {
            ClientId = entry.ClientId ?? _tenant.ClientId ?? PlatformTenant.ClientId,
            ActorType = actorType,
            ActorId = entry.ActorId ?? _currentUser.ActorId,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId.Length > 64 ? entry.EntityId[..64] : entry.EntityId,
            OldValuesJson = entry.OldValues is null ? null : JsonSerializer.Serialize(entry.OldValues, Json),
            NewValuesJson = entry.NewValues is null ? null : JsonSerializer.Serialize(entry.NewValues, Json),
            IpAddress = _request.IpAddress,
            UserAgent = _request.UserAgent is { Length: > 300 } ua ? ua[..300] : _request.UserAgent,
            CorrelationId = _request.CorrelationId,
            OccurredAt = _time.GetUtcNow().UtcDateTime,
        });
    }
}
