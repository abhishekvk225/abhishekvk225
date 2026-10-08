using System.Text;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Application.Dashboards;

namespace NexaVerify.Api.Http;

/// <summary>
/// Streams CSV lines to the response as they are produced (no buffering of the whole file). Headers go out before the first row, so
/// a failure mid-stream aborts the transfer rather than ending it cleanly with a truncated file.
/// </summary>
public sealed class CsvStreamResult : IActionResult
{
    private readonly UsageReportStream _report;

    public CsvStreamResult(UsageReportStream report)
    {
        _report = report;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{_report.FileName}\"";
        response.Headers.CacheControl = "no-store";

        var cancellationToken = context.HttpContext.RequestAborted;
        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await foreach (var line in _report.Lines.WithCancellation(cancellationToken))
        {
            await writer.WriteAsync(line.AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
    }
}
