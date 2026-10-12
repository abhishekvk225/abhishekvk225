using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexaVerify.Application.Dashboards;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Application.Verification.Query;

/// <summary>A rendered download: bytes plus the type we decided (never the one a caller declared).</summary>
public sealed record DownloadContent(byte[] Content, string ContentType, string FileName)
{
    public override string ToString() => nameof(DownloadContent);
}

/// <summary>
/// Report renderers. Every value is HTML-encoded or CSV-neutralised, nothing is rendered as markup, and the fixed disclaimer is always
/// included. Reports repeat what the result already shows (masked identifiers only).
/// </summary>
public static class VerificationReports
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static DownloadContent Json(VerificationResultDto result, DateTime now) =>
        new(JsonSerializer.SerializeToUtf8Bytes(new VerificationReportDto(result, now, VerificationDtoMapper.Disclaimer), JsonOptions), "application/json", $"verification-{result.RequestId:N}.json");

    public static DownloadContent Html(VerificationResultDto r, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Verification report</title>")
          .Append("<style>body{font-family:sans-serif;margin:2rem;max-width:60rem}table{border-collapse:collapse;width:100%;margin:1rem 0}th,td{border:1px solid #ccc;padding:.4rem;text-align:left}th{background:#f3f3f3}</style></head><body>");
        sb.Append("<h1>Verification report</h1>");
        sb.Append("<p><strong>Request:</strong> ").Append(E(r.RequestId.ToString())).Append(" &middot; <strong>Type:</strong> ").Append(E(r.Type))
          .Append(" &middot; <strong>Status:</strong> ").Append(E(r.Status)).Append(" &middot; <strong>Generated:</strong> ").Append(E(now.ToString("u", CultureInfo.InvariantCulture))).Append("</p>");
        sb.Append("<h2>Stages</h2><ul>")
          .Append("<li>Uploaded: ").Append(E(r.Stages.Uploaded)).Append("</li>")
          .Append("<li>Extracted: ").Append(E(r.Stages.Extracted)).Append("</li>")
          .Append("<li>Rules checked: ").Append(E(r.Stages.RulesChecked)).Append("</li>")
          .Append("<li>Consistency checked: ").Append(E(r.Stages.ConsistencyChecked)).Append("</li>")
          .Append("<li>Official verification: ").Append(E(r.External.State)).Append(r.External.ProviderId is null ? string.Empty : " (" + E(r.External.ProviderId) + ")").Append("</li></ul>");
        sb.Append("<p><strong>Assurance:</strong> ").Append(E(r.Assurance)).Append(r.ReviewRequired ? " &middot; Needs review" : string.Empty).Append("</p>");

        if (r.ReviewReasons.Count > 0)
        {
            sb.Append("<h2>Review reasons</h2><ul>");
            foreach (var reason in r.ReviewReasons)
            {
                sb.Append("<li>").Append(E(reason.Code)).Append(": ").Append(E(reason.Message)).Append("</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("<h2>Checks</h2><table><tr><th>Code</th><th>Stage</th><th>Outcome</th><th>Detail</th></tr>");
        foreach (var c in r.Checks)
        {
            sb.Append("<tr><td>").Append(E(c.Code)).Append("</td><td>").Append(E(c.Stage)).Append("</td><td>").Append(E(c.Outcome)).Append("</td><td>").Append(E(c.Message)).Append("</td></tr>");
        }

        sb.Append("</table>");
        if (r.FieldMatches.Count > 0)
        {
            sb.Append("<h2>Field comparison</h2><table><tr><th>Field</th><th>Outcome</th></tr>");
            foreach (var m in r.FieldMatches)
            {
                sb.Append("<tr><td>").Append(E(m.Field)).Append("</td><td>").Append(E(m.Outcome)).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        if (r.Extracted is { } x)
        {
            sb.Append("<h2>Extracted details</h2>");
            if (x.Pan is { } pan)
            {
                sb.Append("<p>PAN (masked): ").Append(E(pan.PanMasked)).Append("</p>");
            }

            if (x.Passport is { } pp)
            {
                sb.Append("<p>Passport number (masked): ").Append(E(pp.PassportNumberMasked)).Append("</p>");
            }

            if (x.BankStatement is { } bank)
            {
                sb.Append("<p>Account (masked): ").Append(E(bank.AccountNumberMasked ?? string.Empty)).Append(" &middot; transactions: ").Append(bank.TransactionCount.ToString(CultureInfo.InvariantCulture)).Append("</p>");
            }
        }

        sb.Append("<p><em>").Append(E(VerificationDtoMapper.Disclaimer)).Append("</em></p></body></html>");
        return new DownloadContent(Encoding.UTF8.GetBytes(sb.ToString()), "text/html; charset=utf-8", $"verification-{r.RequestId:N}.html");
    }

    public static DownloadContent TransactionsCsv(Guid requestId, IEnumerable<BankTransactionDto> transactions)
    {
        var sb = new StringBuilder();
        sb.Append(CsvFormat.Line("index", "date", "valueDate", "description", "reference", "debit", "credit", "balance"));
        foreach (var t in transactions)
        {
            sb.Append(CsvFormat.Line(t.Index, t.Date, t.ValueDate, t.Description, t.Reference, Money(t.Debit), Money(t.Credit), Money(t.Balance)));
        }

        // The BOM makes spreadsheet software read non-ASCII descriptions correctly (same as the other CSV exports).
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new DownloadContent(bytes, "text/csv; charset=utf-8", $"verification-{requestId:N}-transactions.csv");
    }

    /// <summary>Amounts are formatted by us; the shared CSV writer still neutralises a leading minus (a negative balance becomes text), which is the safe side.</summary>
    private static string? Money(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture);

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
