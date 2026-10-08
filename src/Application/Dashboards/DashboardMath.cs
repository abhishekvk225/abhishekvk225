using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Dashboards;

/// <summary>How a recognition outcome counts on a dashboard (docs/01 §8): successful, a valid "no match" answer, or failed.</summary>
public enum OutcomeClass
{
    Successful = 0,
    NoMatch,
    Failed,
}

/// <summary>Pure helpers shared by the dashboard services (and unit-tested on their own).</summary>
public static class DashboardMath
{
    public static OutcomeClass Classify(RecognitionOutcome outcome) => outcome switch
    {
        RecognitionOutcome.Enrolled or RecognitionOutcome.Matched => OutcomeClass.Successful,
        RecognitionOutcome.NoMatch => OutcomeClass.NoMatch,
        _ => OutcomeClass.Failed,
    };

    /// <summary>The last <paramref name="days"/> whole UTC days up to and including today: [From, ToExclusive).</summary>
    public static (DateTime From, DateTime ToExclusive) Window(DateTime now, int days)
    {
        var today = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        return (today.AddDays(1 - days), today.AddDays(1));
    }

    public static IReadOnlyList<DateOnly> Days(DateTime from, DateTime toExclusive)
    {
        var list = new List<DateOnly>();
        for (var d = from.Date; d < toExclusive; d = d.AddDays(1))
        {
            list.Add(DateOnly.FromDateTime(d));
        }

        return list;
    }

    /// <summary>A share in [0,1] rounded to four places; 0 when there is nothing to divide by.</summary>
    public static decimal Rate(long part, long whole) => whole <= 0 ? 0m : Math.Round((decimal)part / whole, 4);

    /// <summary>
    /// The value below which <paramref name="quantile"/> of the observations fall, from a histogram of fixed-width buckets
    /// (bucket b covers [b·width, (b+1)·width)). Returns the bucket's upper edge — a slight over-estimate, never an under-estimate —
    /// or null when there are no observations.
    /// </summary>
    public static int? Percentile(IEnumerable<(int Bucket, long Count)> histogram, double quantile, int bucketWidth)
    {
        var buckets = histogram.Where(h => h.Count > 0).OrderBy(h => h.Bucket).ToList();
        var total = buckets.Sum(b => b.Count);
        if (total == 0)
        {
            return null;
        }

        var rank = (long)Math.Ceiling(quantile * total);
        long seen = 0;
        foreach (var (bucket, count) in buckets)
        {
            seen += count;
            if (seen >= rank)
            {
                return (bucket + 1) * bucketWidth;
            }
        }

        return (buckets[^1].Bucket + 1) * bucketWidth;
    }

    public static IReadOnlyList<RecognitionDayDto> RecognitionsPerDay(IReadOnlyList<DateOnly> days, IReadOnlyList<RecognitionDayRow> rows)
    {
        var byDay = rows.GroupBy(r => DateOnly.FromDateTime(r.Day)).ToDictionary(g => g.Key);
        return days.Select(day =>
        {
            if (!byDay.TryGetValue(day, out var g))
            {
                return new RecognitionDayDto(day, 0, 0, 0, 0);
            }

            long Sum(OutcomeClass c) => g.Where(r => Classify(r.Outcome) == c).Sum(r => r.Count);
            var ok = Sum(OutcomeClass.Successful);
            var none = Sum(OutcomeClass.NoMatch);
            var failed = Sum(OutcomeClass.Failed);
            return new RecognitionDayDto(day, ok + none + failed, ok, none, failed);
        }).ToList();
    }

    public static RecognitionSummaryDto Summarise(IReadOnlyList<RecognitionDayDto> perDay)
    {
        var total = perDay.Sum(d => d.Total);
        var ok = perDay.Sum(d => d.Successful);
        var none = perDay.Sum(d => d.NoMatch);
        var failed = perDay.Sum(d => d.Failed);
        return new RecognitionSummaryDto(total, ok, none, failed, Rate(ok, total), Rate(none, total), Rate(failed, total));
    }

    /// <summary>Consumed and refunded credits per day from ledger rows (a consumption is a negative ledger amount; both are reported as positives).</summary>
    public static IReadOnlyList<CreditDayDto> CreditsPerDay(IReadOnlyList<DateOnly> days, IReadOnlyList<LedgerDayRow> rows)
    {
        var byDay = rows.GroupBy(r => DateOnly.FromDateTime(r.Day)).ToDictionary(g => g.Key);
        return days.Select(day =>
        {
            if (!byDay.TryGetValue(day, out var g))
            {
                return new CreditDayDto(day, 0, 0, 0);
            }

            var consumed = -g.Where(r => r.Type == LedgerEntryType.Consume).Sum(r => r.Credits);
            var refunded = g.Where(r => r.Type == LedgerEntryType.Refund).Sum(r => r.Credits);
            return new CreditDayDto(day, consumed, refunded, consumed - refunded);
        }).ToList();
    }

    public static CreditSummaryDto Summarise(IReadOnlyList<CreditDayDto> perDay)
    {
        var consumed = perDay.Sum(d => d.Consumed);
        var refunded = perDay.Sum(d => d.Refunded);
        return new CreditSummaryDto(consumed, refunded, consumed - refunded);
    }

    public static IReadOnlyList<ApiDayDto> ApiPerDay(
        IReadOnlyList<DateOnly> days, IReadOnlyList<ApiDayRow> rows, IReadOnlyList<LatencyBucketRow> histogram, double quantile, int bucketWidth)
    {
        var byDay = rows.ToDictionary(r => DateOnly.FromDateTime(r.Day));
        var latency = histogram.GroupBy(h => DateOnly.FromDateTime(h.Day)).ToDictionary(g => g.Key, g => g.Select(h => (h.Bucket, h.Count)).ToList());
        return days.Select(day =>
        {
            byDay.TryGetValue(day, out var row);
            int? p = latency.TryGetValue(day, out var h) ? Percentile(h, quantile, bucketWidth) : null;
            return new ApiDayDto(day, row?.Requests ?? 0, row?.Errors ?? 0, row?.ServerErrors ?? 0, p);
        }).ToList();
    }

    public static ApiSummaryDto Summarise(IReadOnlyList<ApiDayDto> perDay, IReadOnlyList<LatencyBucketRow> histogram, double quantile, int bucketWidth)
    {
        var requests = perDay.Sum(d => d.Requests);
        var errors = perDay.Sum(d => d.Errors);
        var combined = histogram.GroupBy(h => h.Bucket).Select(g => (g.Key, g.Sum(h => h.Count)));
        return new ApiSummaryDto(requests, errors, perDay.Sum(d => d.ServerErrors), Rate(errors, requests), Percentile(combined, quantile, bucketWidth));
    }

    /// <summary>Folds (stored status, past end date) counts into effective-status counts using the one domain rule.</summary>
    public static IReadOnlyList<CountByLabelDto> EffectiveStatusCounts(IReadOnlyList<LicenseStatusCount> rows, DateTime now) =>
        rows.GroupBy(r => License.EffectiveStatusOf(r.Status, r.PastEnd ? now : DateTime.MaxValue, now))
            .Select(g => new CountByLabelDto(g.Key.ToString(), g.Sum(r => r.Count)))
            .OrderBy(c => c.Label, StringComparer.Ordinal)
            .ToList();

    public static int PercentRemaining(int total, int consumed) => total <= 0 ? 0 : (int)Math.Floor(100.0 * (total - consumed) / total);
}
