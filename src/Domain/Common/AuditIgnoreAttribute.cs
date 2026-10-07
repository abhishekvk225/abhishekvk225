namespace NexaVerify.Domain.Common;

/// <summary>Property is excluded from audit old/new value capture (secrets, hashes, biometric data).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute
{
}
