using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using NexaVerify.Contracts.Common;
using Serilog.Core;
using Serilog.Events;

namespace NexaVerify.Api.Logging;

/// <summary>
/// Masks secrets when objects are destructured into logs (<c>{@Request}</c>): properties/fields marked
/// <see cref="SensitiveAttribute"/>, a name-based safety net, dictionary entries with sensitive keys, and binary payloads
/// (images, embeddings) which are replaced by their length. Exceptions are left to Serilog's exception handling.
/// </summary>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    public const string Mask = "***";

    private static readonly string[] SensitiveFragments =
    [
        "password", "passwd", "secret", "token", "apikey", "api_key", "authorization", "credential", "embedding",
        "privatekey", "connectionstring", "base64", "passcode", "sessionid",
    ];

    private static readonly HashSet<string> SensitiveExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "code", "otp", "pin", "hash", "salt", "cookie", "signature", "template", "image", "photo", "pem",
    };

    private static readonly ConcurrentDictionary<Type, Member[]> Cache = new();

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        switch (value)
        {
            case byte[] bytes:
                result = new ScalarValue($"<{bytes.Length} bytes>");
                return true;
            case Stream:
                result = new ScalarValue("<stream>");
                return true;
            case Exception:
                result = null;
                return false;
            case IDictionary dictionary:
                result = DestructureDictionary(dictionary, propertyValueFactory);
                return true;
        }

        var type = value.GetType();
        if (type.Namespace is null || !type.Namespace.StartsWith("NexaVerify", StringComparison.Ordinal))
        {
            result = null;
            return false;
        }

        var properties = new List<LogEventProperty>();
        foreach (var member in Cache.GetOrAdd(type, BuildMembers))
        {
            object? memberValue;
            try
            {
                memberValue = member.Read(value);
            }
            catch
            {
                continue;
            }

            LogEventPropertyValue logValue = member.Sensitive
                ? new ScalarValue(Mask)
                : propertyValueFactory.CreatePropertyValue(memberValue, destructureObjects: true);
            properties.Add(new LogEventProperty(member.Name, logValue));
        }

        result = new StructureValue(properties, type.Name);
        return true;
    }

    /// <summary>Binary payloads (images, embeddings, key material) never belong in logs, not even a hex prefix.</summary>
    private static bool IsBinary(Type type) =>
        type == typeof(byte[]) || typeof(Stream).IsAssignableFrom(type)
        || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>) || type == typeof(float[]);

    public static bool IsSensitiveName(string name) =>
        SensitiveExactNames.Contains(name) || SensitiveFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static LogEventPropertyValue DestructureDictionary(IDictionary dictionary, ILogEventPropertyValueFactory factory)
    {
        var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            LogEventPropertyValue value = IsSensitiveName(key)
                ? new ScalarValue(Mask)
                : factory.CreatePropertyValue(entry.Value, destructureObjects: true);
            entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue(key), value));
        }

        return new DictionaryValue(entries);
    }

    private static Member[] BuildMembers(Type type)
    {
        var members = new List<Member>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            var sensitive = property.GetCustomAttribute<SensitiveAttribute>() is not null
                || IsSensitiveName(property.Name)
                || IsBinary(property.PropertyType);
            members.Add(new Member(property.Name, sensitive, o => property.GetValue(o)));
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var sensitive = field.GetCustomAttribute<SensitiveAttribute>() is not null
                || IsSensitiveName(field.Name)
                || IsBinary(field.FieldType);
            members.Add(new Member(field.Name, sensitive, o => field.GetValue(o)));
        }

        return [.. members];
    }

    private sealed record Member(string Name, bool Sensitive, Func<object, object?> Read);
}
