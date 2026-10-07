namespace NexaVerify.Contracts.Common;

/// <summary>Value must never appear in logs or error output; destructuring masks it.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SensitiveAttribute : Attribute
{
}
