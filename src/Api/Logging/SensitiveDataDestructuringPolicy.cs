using System.Reflection;
using NexaVerify.Contracts.Common;
using Serilog.Core;
using Serilog.Events;

namespace NexaVerify.Api.Logging;

/// <summary>
/// Masks secrets when objects are destructured into logs (<c>{@Request}</c>): properties marked
/// <see cref="SensitiveAttribute"/> plus a name-based safety net (password, token, secret, key, authorization, embedding).
/// </summary>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    public const string Mask = "***";

    private static readonly string[] SensitiveNameFragments =
    [
        "password", "secret", "token", "apikey", "authorization", "credential", "embedding", "privatekey",
    ];

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        result = null;
        var type = value.GetType();

        // Only handle our own DTOs/entities; leave BCL and framework types to Serilog defaults.
        if (type.Namespace is null || !type.Namespace.StartsWith("NexaVerify", StringComparison.Ordinal) || type.IsEnum)
        {
            return false;
        }

        var properties = new List<LogEventProperty>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            LogEventPropertyValue logValue = IsSensitive(property)
                ? new ScalarValue(Mask)
                : propertyValueFactory.CreatePropertyValue(propertyValue, destructureObjects: true);
            properties.Add(new LogEventProperty(property.Name, logValue));
        }

        result = new StructureValue(properties, type.Name);
        return true;
    }

    private static bool IsSensitive(PropertyInfo property) =>
        property.GetCustomAttribute<SensitiveAttribute>() is not null
        || SensitiveNameFragments.Any(f => property.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
}
