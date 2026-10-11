using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Infrastructure.Platform.KeyRotation;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Migrator;

/// <summary>
/// Operator commands for master-key rotation (runbook: docs/runbooks/key-rotation.md). Output carries key ids, fingerprints and counts only.
/// Options use the <c>--name=value</c> form (a bare <c>--flag</c> is a switch).
/// </summary>
internal static class KeyRotationCommands
{
    public const int Usage = 2;

    public static async Task<int> RotateAsync(IServiceProvider services, string[] args, ILogger logger, CancellationToken cancellationToken)
    {
        var options = Options.Parse(args);
        var dryRun = options.Has("dry-run");
        if (!dryRun && !options.Has("confirm-backup"))
        {
            logger.LogError("rotate-master-key changes stored data: take a verified backup first, then pass --confirm-backup (or use --dry-run to only count).");
            return Usage;
        }

        if (!options.TryInt("batch-size", 200, 1, RotationRequest.MaxBatchSize, out var batchSize) || !options.TryInt("max-batches", 0, 0, int.MaxValue, out var maxBatches))
        {
            logger.LogError("--batch-size must be 1-{Max}; --max-batches must be a non-negative integer.", RotationRequest.MaxBatchSize);
            return Usage;
        }

        var master = services.GetRequiredService<MasterKeyProvider>();
        PrintRing(master);
        var report = await services.GetRequiredService<MasterKeyRotationService>().RunAsync(
            new RotationRequest(dryRun, batchSize, maxBatches == 0 ? null : maxBatches, options.Has("resume")), cancellationToken);

        Console.WriteLine($"Target key: {report.CurrentKeyId}{(report.DryRun ? " (dry run: nothing changed)" : string.Empty)}");
        if (report.Before is not null)
        {
            PrintUsage("Before", report.Before);
        }

        if (!report.DryRun)
        {
            Console.WriteLine($"Re-wrapped client data keys: {report.ClientKeysRewrapped}");
            Console.WriteLine($"Re-encrypted MFA secrets:    {report.MfaSecretsReencrypted}");
            Console.WriteLine($"Ledgers re-anchored:         {report.LicensesReanchored} ({report.CheckpointsResigned} checkpoint(s) re-signed and the originals superseded)");
            Console.WriteLine($"Rows changed concurrently (re-checked): {report.Contended}; undecryptable rows left untouched: {report.UndecryptableRows}; batches: {report.BatchesRun}");
            if (report.After is not null)
            {
                PrintUsage("After", report.After);
            }
        }

        foreach (var broken in report.BrokenLedgers)
        {
            Console.WriteLine($"  BROKEN license {broken.LicenseId}: {broken.Reason}");
        }

        Console.WriteLine($"Result: {report.Outcome}{(report.Message is null ? string.Empty : " - " + report.Message)}");
        if (report.Outcome == RotationOutcome.Incomplete)
        {
            Console.WriteLine("Run again with --resume until the result is Complete.");
        }

        return (int)report.Outcome;
    }

    public static async Task<int> StatusAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var master = services.GetRequiredService<MasterKeyProvider>();
        PrintRing(master);
        var usage = await services.GetRequiredService<KeyUsageSurvey>().SurveyAsync(cancellationToken);
        PrintUsage("Usage", usage);
        Console.WriteLine($"Complete: {usage.PercentComplete.ToString("0.##", CultureInfo.InvariantCulture)}% under the current key '{usage.CurrentKeyId}'");
        foreach (var info in master.Keys.Where(k => !k.IsCurrent))
        {
            Console.WriteLine($"Safe to retire '{info.Id}': {(usage.IsSafeToRetire(info.Id) ? "YES (no live data references it; keep it vaulted for older backups)" : "NO")}");
        }

        if (usage.UnknownKeyIds.Count > 0)
        {
            Console.WriteLine($"WARNING: live data references key id(s) not in the keyring: {string.Join(", ", usage.UnknownKeyIds)}");
            return 3;
        }

        return 0;
    }

    public static async Task<int> VerifyAsync(IServiceProvider services, string[] args, ILogger logger, CancellationToken cancellationToken)
    {
        var options = Options.Parse(args);
        if (!options.TryInt("sample", 25, 1, 10_000, out var sample))
        {
            logger.LogError("--sample must be 1-10000.");
            return Usage;
        }

        var master = services.GetRequiredService<MasterKeyProvider>();
        PrintRing(master);
        var result = await services.GetRequiredService<KeyringVerifier>().VerifyAsync(sample, cancellationToken);
        PrintUsage("Usage", result.Usage);
        Console.WriteLine($"Decrypt test: {result.ClientKeysTested} client key(s), {result.MfaSecretsTested} MFA secret(s), {result.CheckpointsTested} checkpoint MAC(s) sampled; {result.Failures.Count} failure(s).");
        foreach (var failure in result.Failures)
        {
            Console.WriteLine($"  FAILED: {failure}");
        }

        if (result.LedgerError is not null)
        {
            Console.WriteLine($"Ledger verification could not run: {result.LedgerError}");
        }
        else
        {
            Console.WriteLine($"Ledger verification: {result.LicensesChecked} license(s), {result.LedgerEntriesChecked} entries, {result.BrokenLicenses} broken.");
        }

        Console.WriteLine(result.ExitCode == 0 ? "Keyring OK." : "Keyring verification FAILED.");
        return result.ExitCode;
    }

    private static void PrintRing(MasterKeyProvider master)
    {
        Console.WriteLine($"Keyring: {master.Keys.Count} key(s); current '{master.CurrentKeyId}'");
        foreach (var key in master.Keys)
        {
            Console.WriteLine($"  {key.Id}  fingerprint {key.Fingerprint}{(key.IsCurrent ? "  (current)" : string.Empty)}");
        }
    }

    private static void PrintUsage(string title, KeyUsageReport usage)
    {
        Console.WriteLine($"{title}:");
        foreach (var artifact in KeyArtifacts.All)
        {
            var parts = usage.Counts.TryGetValue(artifact, out var byKey) && byKey.Count > 0
                ? string.Join(", ", byKey.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"))
                : "none";
            Console.WriteLine($"  {artifact,-16} total {usage.Total(artifact),6}   per key: {parts}");
        }
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            foreach (var arg in args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
            {
                var body = arg[2..];
                var eq = body.IndexOf('=', StringComparison.Ordinal);
                options._values[eq < 0 ? body : body[..eq]] = eq < 0 ? null : body[(eq + 1)..];
            }

            return options;
        }

        public bool Has(string name) => _values.ContainsKey(name);

        public bool TryInt(string name, int fallback, int min, int max, out int value)
        {
            value = fallback;
            if (!_values.TryGetValue(name, out var text))
            {
                return true;
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
        }
    }
}
