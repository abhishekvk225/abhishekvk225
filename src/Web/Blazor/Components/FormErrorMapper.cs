using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Forms;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Components;

/// <summary>Puts API field errors (camelCase names) back onto the matching properties of a form model.</summary>
public static class FormErrorMapper
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, string>> PropertyNames = new();

    /// <summary>
    /// Adds each field message to <paramref name="store"/>. Returns the messages that belong to no property of the form
    /// (to be shown as a general error).
    /// </summary>
    public static List<string> Apply(Type modelType, EditContext context, ValidationMessageStore store, IReadOnlyDictionary<string, string[]>? fieldErrors, out bool anyMapped)
    {
        var general = new List<string>();
        anyMapped = false;
        if (fieldErrors is null)
        {
            return general;
        }

        var map = PropertyNames.GetOrAdd(modelType, t => t.GetProperties().ToDictionary(p => p.Name, p => p.Name, StringComparer.OrdinalIgnoreCase));
        foreach (var (field, messages) in fieldErrors)
        {
            if (map.TryGetValue(field, out var name))
            {
                foreach (var message in messages)
                {
                    store.Add(context.Field(name), message);
                }

                anyMapped = true;
            }
            else
            {
                general.AddRange(messages);
            }
        }

        return general;
    }

    /// <summary>The property a field error belongs to, if the form has one.</summary>
    public static string? PropertyFor(Type modelType, string field)
    {
        var map = PropertyNames.GetOrAdd(modelType, t => t.GetProperties().ToDictionary(p => p.Name, p => p.Name, StringComparer.OrdinalIgnoreCase));
        return map.GetValueOrDefault(field);
    }
}
