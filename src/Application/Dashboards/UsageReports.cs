using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FluentValidation;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;

namespace NexaVerify.Application.Dashboards;

/// <summary>
/// CSV writing that is safe to open in a spreadsheet. A text cell that begins with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or
/// a carriage return would be run as a formula (CSV injection), so such cells are prefixed with an apostrophe; numbers are formatted
/// by us and never come from user input.
/// </summary>
public static class CsvFormat
{
    private static readonly char[] Invisible = ['\u200B', '\u200C', '\u200D', '\u200E', '\u200F', '\u2060', '\uFEFF', '\u00AD'];
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];
    private static readonly SearchValues<char> QuotedChars = SearchValues.Create(",\"\r\n");

    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Also checked after leading spaces: some spreadsheets ignore them when deciding whether a cell is a formula.
        // Zero-width and other format characters (U+200B, U+FEFF ...) are invisible but can precede a formula, so they are skipped too.
        var trimmed = value.TrimStart().TrimStart(Invisible).TrimStart();
        var isFormula = Array.IndexOf(FormulaStarts, value[0]) >= 0 || (trimmed.Length > 0 && Array.IndexOf(FormulaStarts, trimmed[0]) >= 0);
        var cell = isFormula ? "'" + value : value;
        return cell.AsSpan().IndexOfAny(QuotedChars) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
    }

    /// <summary>One CSV record (CRLF-terminated). Strings are neutralised; numbers and dates are written in an invariant format.</summary>
    public static string Line(params object?[] cells)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(cells[i] switch
            {
                null => string.Empty,
                string s => Text(s),
                long n => n.ToString(CultureInfo.InvariantCulture),
                int n => n.ToString(CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Enum e => e.ToString(),
                _ => throw new ArgumentException($"Unsupported CSV cell type {cells[i]!.GetType().Name}."),
            });
        }

        return sb.Append("\r\n").ToString();
    }
}

/// <summary>Resolves the inclusive UTC date range of an export (defaults: the last N days) and enforces its bound.</summary>
public static class ReportRange
{
    public static Result<(DateOnly From, DateOnly To)> Resolve(UsageReportQuery query, DateOnly today, DashboardOptions options)
    {
        var earliest = new DateOnly(2020, 1, 1);
        var latest = today.AddDays(1); // tomorrow in UTC covers clients ahead of the server's date
        if ((query.To is { } t && (t < earliest || t > latest)) || (query.From is { } f && (f < earliest || f > latest)))
        {
            return Error.Validation("The dates are outside the supported range.", new Dictionary<string, string[]> { ["from"] = [$"Dates must be between {earliest:yyyy-MM-dd} and tomorrow."] });
        }

        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(1 - options.DefaultReportDays);
        if (from > to)
        {
            return Error.Validation("'from' must not be after 'to'.", new Dictionary<string, string[]> { ["from"] = ["Must not be after 'to'."] });
        }

        var span = to.DayNumber - from.DayNumber + 1;
        if (span > options.MaxReportDays)
        {
            return Error.Validation(
                $"A report can cover at most {options.MaxReportDays} days.",
                new Dictionary<string, string[]> { ["to"] = [$"The range is {span} days; the maximum is {options.MaxReportDays}."] });
        }

        return (from, to);
    }
}

public sealed class DashboardQueryValidator : AbstractValidator<DashboardQuery>
{
    public DashboardQueryValidator(IOptions<DashboardOptions> options)
    {
        var max = options.Value.MaxDays;
        RuleFor(x => x.Days).InclusiveBetween(1, max).When(x => x.Days.HasValue).WithMessage($"Must be between 1 and {max}.");
    }
}

public sealed class UsageReportQueryValidator : AbstractValidator<UsageReportQuery>
{
    public UsageReportQueryValidator(IOptions<DashboardOptions> options, TimeProvider time)
    {
        RuleFor(x => x).Custom((query, context) =>
        {
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var resolved = ReportRange.Resolve(query, today, options.Value);
            if (resolved.IsFailure)
            {
                foreach (var (field, messages) in resolved.Error!.FieldErrors ?? new Dictionary<string, string[]>())
                {
                    foreach (var message in messages)
                    {
                        context.AddFailure(field, message);
                    }
                }
            }
        });
    }
}

/// <summary>A CSV file produced lazily: lines are generated while the response is written, so a large export never sits in memory.</summary>
public sealed record UsageReportStream(string FileName, IAsyncEnumerable<string> Lines);

public interface IUsageReportService
{
    /// <summary>The caller's own usage per day, operation and outcome.</summary>
    Task<Result<UsageReportStream>> ExportClientAsync(UsageReportQuery query, CancellationToken cancellationToken);

    /// <summary>Platform-wide billed usage per day, client and operation (from the ledger).</summary>
    Task<Result<UsageReportStream>> ExportAdminAsync(UsageReportQuery query, CancellationToken cancellationToken);
}

public sealed class UsageReportService : IUsageReportService
{
    private readonly IDashboardQueries _queries;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _time;

    public UsageReportService(
        IDashboardQueries queries, ICurrentUser currentUser, IAuditService audit, IUnitOfWork unitOfWork, IOptions<DashboardOptions> options, TimeProvider time)
    {
        _queries = queries;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _time = time;
    }

    public async Task<Result<UsageReportStream>> ExportClientAsync(UsageReportQuery query, CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client accounts have a usage report.");
        }

        var range = ResolveRange(query);
        if (range.IsFailure)
        {
            return range.Error!;
        }

        var (from, to) = range.Value;
        await RecordExportAsync("client-usage", clientId, from, to, cancellationToken);
        return new UsageReportStream($"usage-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", ClientLines(from, to, cancellationToken));
    }

    public async Task<Result<UsageReportStream>> ExportAdminAsync(UsageReportQuery query, CancellationToken cancellationToken)
    {
        var range = ResolveRange(query);
        if (range.IsFailure)
        {
            return range.Error!;
        }

        var (from, to) = range.Value;
        await RecordExportAsync("platform-usage", null, from, to, cancellationToken);
        return new UsageReportStream($"platform-usage-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", AdminLines(from, to, cancellationToken));
    }

    private Result<(DateOnly From, DateOnly To)> ResolveRange(UsageReportQuery query) =>
        ReportRange.Resolve(query, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), _options);

    private async Task RecordExportAsync(string report, Guid? clientId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        _audit.Record(new AuditEntry("report.exported", "UsageReport", report, clientId, NewValues: new { Report = report, From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), To = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static (DateTime From, DateTime ToExclusive) Bounds(DateOnly from, DateOnly to) =>
        (DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc), DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));

    private async IAsyncEnumerable<string> ClientLines(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return CsvFormat.Line("date", "operation", "outcome", "requests", "credits_charged");
        var (start, end) = Bounds(from, to);
        await foreach (var row in _queries.StreamClientUsageAsync(start, end, cancellationToken))
        {
            yield return CsvFormat.Line(DateOnly.FromDateTime(row.Day), row.Operation.ToString(), row.Outcome.ToString(), row.Requests, row.CreditsCharged);
        }
    }

    private async IAsyncEnumerable<string> AdminLines(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return CsvFormat.Line("date", "client_code", "client_name", "operation", "billed_operations", "credits_consumed", "refunds", "credits_refunded");
        var (start, end) = Bounds(from, to);
        await foreach (var row in _queries.StreamAdminUsageAsync(start, end, cancellationToken))
        {
            yield return CsvFormat.Line(
                DateOnly.FromDateTime(row.Day), row.ClientCode, row.ClientName, row.Operation?.ToString(), row.BilledOperations, row.CreditsConsumed, row.Refunds, row.CreditsRefunded);
        }
    }
}
