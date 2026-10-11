using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NexaVerify.Api.Http;

/// <summary>JSON body of <c>/health/keyring</c>: status plus the check's data (key ids and booleans). Never key material.</summary>
public static class KeyringHealthResponse
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var entry = report.Entries.Values.FirstOrDefault();
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            description = entry.Description,
            currentKeyId = entry.Data.GetValueOrDefault("currentKeyId"),
            keyIds = entry.Data.GetValueOrDefault("keyIds"),
            unknownKeyIdsInUse = entry.Data.GetValueOrDefault("unknownKeyIdsInUse"),
            rewrapPending = entry.Data.GetValueOrDefault("rewrapPending"),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
