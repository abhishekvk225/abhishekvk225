using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace NexaVerify.Load;

public enum LoadOperation
{
    Verify,
    Enroll,
    Identify,
}

/// <param name="Concurrency">Parallel workers, each sending one request at a time.</param>
/// <param name="Duration">Stop after this long (ignored when <paramref name="TotalRequests"/> is set).</param>
/// <param name="TotalRequests">Stop after this many requests in total (deterministic runs, tests).</param>
/// <param name="Profiles">People enrolled before the run so that verify and identify have something to match.</param>
/// <param name="Mix">Relative weights of the operations; operations with weight 0 are not used.</param>
public sealed record LoadOptions(
    int Concurrency = 4,
    TimeSpan? Duration = null,
    int? TotalRequests = null,
    int Profiles = 20,
    IReadOnlyDictionary<LoadOperation, int>? Mix = null,
    int Seed = 1)
{
    public static IReadOnlyDictionary<LoadOperation, int> DefaultMix { get; } =
        new Dictionary<LoadOperation, int> { [LoadOperation.Verify] = 70, [LoadOperation.Identify] = 20, [LoadOperation.Enroll] = 10 };
}

public sealed record LatencyStats(int Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs)
{
    public static LatencyStats From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return new LatencyStats(0, 0, 0, 0, 0, 0);
        }

        var sorted = samples.Order().ToArray();
        return new LatencyStats(sorted.Length, sorted.Average(), Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    public static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

public sealed record LoadReport(
    TimeSpan Elapsed,
    int Requests,
    int Succeeded,
    int RateLimited,
    int Failed,
    IReadOnlyDictionary<int, int> StatusCodes,
    IReadOnlyDictionary<LoadOperation, LatencyStats> Latency,
    LatencyStats Overall)
{
    public double RequestsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Requests / Elapsed.TotalSeconds;

    /// <summary>Share of requests that failed for a reason other than being rate limited (5xx, 4xx, network errors).</summary>
    public double ErrorRate => Requests == 0 ? 0 : (double)Failed / Requests;

    public string Summary()
    {
        var lines = new List<string>
        {
            $"{Requests} requests in {Elapsed.TotalSeconds:F1}s = {RequestsPerSecond:F1} req/s; ok {Succeeded}, rate-limited {RateLimited}, failed {Failed} ({ErrorRate:P2})",
            "status codes: " + string.Join(", ", StatusCodes.OrderBy(s => s.Key).Select(s => $"{s.Key}x{s.Value}")),
            $"all   : p50 {Overall.P50Ms:F0} ms  p95 {Overall.P95Ms:F0} ms  p99 {Overall.P99Ms:F0} ms  max {Overall.MaxMs:F0} ms",
        };
        lines.AddRange(Latency.OrderBy(l => l.Key).Select(l => $"{l.Key,-8}: n={l.Value.Count} p50 {l.Value.P50Ms:F0} ms  p95 {l.Value.P95Ms:F0} ms  p99 {l.Value.P99Ms:F0} ms"));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Drives the face API the way an integrator would (API key, multipart upload, one Idempotency-Key per request): enrols a pool of
/// people, then mixes verify / identify / enroll across parallel workers and reports status codes and latency percentiles.
/// </summary>
public static class LoadRunner
{
    private const string Faces = "api/v1/faces/";

    public static async Task<LoadReport> RunAsync(HttpClient http, LoadOptions options, CancellationToken cancellationToken)
    {
        if (options.Concurrency < 1 || (options.Duration is null && options.TotalRequests is null))
        {
            throw new ArgumentException("Concurrency must be at least 1 and either Duration or TotalRequests must be set.", nameof(options));
        }

        var mix = (options.Mix ?? LoadOptions.DefaultMix).Where(m => m.Value > 0).ToArray();
        if (mix.Length == 0)
        {
            throw new ArgumentException("The operation mix is empty.", nameof(options));
        }

        var run = Guid.NewGuid().ToString("N")[..8];
        var profiles = Math.Max(options.Profiles, 1);
        for (var i = 0; i < profiles; i++)
        {
            using var response = await SendAsync(http, "enroll", FaceImages.Person(Seed(options, i)), new() { ["externalRef"] = $"load-{run}-{i}", ["consentReference"] = "load-test" }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Could not enrol load-test person {i}: HTTP {(int)response.StatusCode}. Check the API key scopes, the license balance and the key's rate limit.");
            }
        }

        var samples = new List<(LoadOperation Operation, int Status, double Ms)>();
        var gate = new object();
        var issued = 0;
        var watch = Stopwatch.StartNew();
        var deadline = options.Duration is { } d && options.TotalRequests is null ? d : (TimeSpan?)null;

        await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(worker => Task.Run(async () =>
        {
            var random = new Random(options.Seed + (worker * 7919));
            var local = new List<(LoadOperation, int, double)>();
            while (!cancellationToken.IsCancellationRequested)
            {
                int ticket;
                lock (gate)
                {
                    ticket = ++issued;
                }

                if ((options.TotalRequests is { } total && ticket > total) || (deadline is { } limit && watch.Elapsed >= limit))
                {
                    break;
                }

                var operation = Pick(mix, random);
                var person = random.Next(profiles);
                var started = Stopwatch.GetTimestamp();
                int status;
                try
                {
                    using var response = operation switch
                    {
                        LoadOperation.Verify => await SendAsync(http, "verify", FaceImages.Person(Seed(options, person), variation: 1 + random.Next(5)), new() { ["externalRef"] = $"load-{run}-{person}" }, cancellationToken),
                        LoadOperation.Identify => await SendAsync(http, "identify", FaceImages.Person(Seed(options, person), variation: 1 + random.Next(5)), null, cancellationToken),
                        _ => await SendAsync(http, "enroll", FaceImages.Person(Seed(options, 10_000 + ticket)), new() { ["externalRef"] = $"load-{run}-new-{ticket}", ["consentReference"] = "load-test" }, cancellationToken),
                    };
                    status = (int)response.StatusCode;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    status = 0; // network error or client-side timeout
                }

                local.Add((operation, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }

            lock (gate)
            {
                samples.AddRange(local);
            }
        }, cancellationToken)));
        watch.Stop();

        var codes = samples.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count());
        var limited = samples.Count(s => s.Status == (int)HttpStatusCode.TooManyRequests);
        var ok = samples.Count(s => s.Status is >= 200 and < 300);
        return new LoadReport(
            watch.Elapsed,
            samples.Count,
            ok,
            limited,
            samples.Count - ok - limited,
            codes,
            samples.GroupBy(s => s.Operation).ToDictionary(g => g.Key, g => LatencyStats.From(g.Select(s => s.Ms).ToList())),
            LatencyStats.From(samples.Select(s => s.Ms).ToList()));
    }

    private static int Seed(LoadOptions options, int person) => (options.Seed * 100_003) + person;

    private static LoadOperation Pick(KeyValuePair<LoadOperation, int>[] mix, Random random)
    {
        var roll = random.Next(mix.Sum(m => m.Value));
        foreach (var (operation, weight) in mix)
        {
            if ((roll -= weight) < 0)
            {
                return operation;
            }
        }

        return mix[^1].Key;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string operation, byte[] image, Dictionary<string, string>? fields, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? [])
        {
            form.Add(new StringContent(value), key);
        }

        var part = new ByteArrayContent(image);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "image", "photo.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, Faces + operation) { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await http.SendAsync(request, cancellationToken);
    }
}
