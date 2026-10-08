using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Dashboards;

namespace NexaVerify.Api.Http;

/// <summary>
/// Streams CSV lines to the response as they are produced (no buffering of the whole file), UTF-8 with a byte-order mark so
/// spreadsheet software reads non-ASCII text correctly.
/// <list type="bullet">
/// <item>The header and the first data row are produced BEFORE the response starts, so a failure there (database down, bad range) is still a normal
/// 500 problem response instead of an empty "successful" file.</item>
/// <item>A failure after the first bytes were sent cannot change the status code. The connection is aborted so the client sees a
/// failed transfer, not a clean-looking file that silently stops early; the failure is logged (without any row content).</item>
/// </list>
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
        var http = context.HttpContext;
        var cancellationToken = http.RequestAborted;
        await using var lines = _report.Lines.GetAsyncEnumerator(cancellationToken);

        // Nothing has been sent yet: an exception here reaches the global handler and becomes a proper ProblemDetails response.
        // The header line comes without touching the database, so the first DATA row is pulled too (that is where the query starts).
        var primed = new List<string>(2);
        while (primed.Count < 2 && await lines.MoveNextAsync())
        {
            primed.Add(lines.Current);
        }

        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{_report.FileName}\"";
        response.Headers.CacheControl = "no-store";

        try
        {
            await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            foreach (var line in primed)
            {
                await writer.WriteAsync(line.AsMemory(), cancellationToken);
            }

            while (primed.Count == 2 && await lines.MoveNextAsync())
            {
                await writer.WriteAsync(lines.Current.AsMemory(), cancellationToken);
            }

            await writer.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the client went away
        }
        catch (Exception ex)
        {
            http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<CsvStreamResult>()
                .LogError(ex, "CSV export {FileName} failed after the response had started; the connection was aborted", _report.FileName);
            http.Abort();
        }
    }
}
