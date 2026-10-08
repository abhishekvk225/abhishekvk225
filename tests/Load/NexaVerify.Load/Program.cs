using System.Globalization;
using NexaVerify.Load;

// NexaVerify.Load — a small load driver for the face API (see tests/Load/README.md).
//   run       drive verify / identify / enroll against a running API with an API key; exit code 1 when a threshold is broken
//   fixtures  write synthetic JPEG fixtures for the k6 scripts
// Examples:
//   dotnet run --project tests/Load/NexaVerify.Load -- run --base-url https://api.example --api-key $NV_API_KEY --concurrency 8 --duration 60
//   dotnet run --project tests/Load/NexaVerify.Load -- fixtures --out tests/Load/k6/fixtures --count 20

var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "run";
string? Option(string name, string? fallback = null)
{
    var index = Array.IndexOf(args, "--" + name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

try
{
    switch (command)
    {
        case "fixtures":
            var directory = Option("out") ?? "fixtures";
            Directory.CreateDirectory(directory);
            var count = int.Parse(Option("count", "20")!, CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"person-{i}.jpg"), FaceImages.Person(i));
                File.WriteAllBytes(Path.Combine(directory, $"person-{i}-b.jpg"), FaceImages.Person(i, variation: 2));
            }

            Console.WriteLine($"Wrote {count * 2} images to {Path.GetFullPath(directory)}");
            return 0;

        case "run":
            var baseUrl = Option("base-url", Environment.GetEnvironmentVariable("NV_BASE_URL"));
            var apiKey = Option("api-key", Environment.GetEnvironmentVariable("NV_API_KEY"));
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine("--base-url (or NV_BASE_URL) and --api-key (or NV_API_KEY) are required.");
                return 2;
            }

            using (var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) })
            {
                http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
                var options = new LoadOptions(
                    Concurrency: int.Parse(Option("concurrency", "4")!, CultureInfo.InvariantCulture),
                    Duration: TimeSpan.FromSeconds(int.Parse(Option("duration", "30")!, CultureInfo.InvariantCulture)),
                    Profiles: int.Parse(Option("profiles", "20")!, CultureInfo.InvariantCulture),
                    Mix: new Dictionary<LoadOperation, int>
                    {
                        [LoadOperation.Verify] = int.Parse(Option("verify", "70")!, CultureInfo.InvariantCulture),
                        [LoadOperation.Identify] = int.Parse(Option("identify", "20")!, CultureInfo.InvariantCulture),
                        [LoadOperation.Enroll] = int.Parse(Option("enroll", "10")!, CultureInfo.InvariantCulture),
                    });
                var report = await LoadRunner.RunAsync(http, options, CancellationToken.None);
                Console.WriteLine(report.Summary());

                // Budgets from docs/01 §10: p95 <= 800 ms verify, <= 1.5 s identify; errors (not 429s) under 1 %.
                var maxError = double.Parse(Option("max-error-rate", "0.01")!, CultureInfo.InvariantCulture);
                var verifyBudget = double.Parse(Option("verify-p95-ms", "800")!, CultureInfo.InvariantCulture);
                var identifyBudget = double.Parse(Option("identify-p95-ms", "1500")!, CultureInfo.InvariantCulture);
                var problems = new List<string>();
                if (report.ErrorRate > maxError)
                {
                    problems.Add($"error rate {report.ErrorRate:P2} is above {maxError:P2}");
                }

                if (report.Latency.TryGetValue(LoadOperation.Verify, out var verify) && verify.P95Ms > verifyBudget)
                {
                    problems.Add($"verify p95 {verify.P95Ms:F0} ms is above {verifyBudget:F0} ms");
                }

                if (report.Latency.TryGetValue(LoadOperation.Identify, out var identify) && identify.P95Ms > identifyBudget)
                {
                    problems.Add($"identify p95 {identify.P95Ms:F0} ms is above {identifyBudget:F0} ms");
                }

                if (report.RateLimited > 0)
                {
                    Console.WriteLine($"note: {report.RateLimited} requests were rate limited (429): raise api.rateLimitPerMinute / api.dailyQuota for the load tenant to measure the engine instead of the limiter.");
                }

                foreach (var problem in problems)
                {
                    Console.Error.WriteLine("THRESHOLD BROKEN: " + problem);
                }

                return problems.Count == 0 ? 0 : 1;
            }

        default:
            Console.Error.WriteLine("Unknown command. Use: run | fixtures");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
