using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Infrastructure.Verification.Documents;

namespace NexaVerify.Infrastructure.Verification.Ocr;

/// <summary>
/// Tesseract as an out-of-process command (no shell, argument list, image on stdin, TSV on stdout). A crash, hang or memory blow-up
/// stays in the child process: it is killed (with its tree) on timeout or cancellation, its output is size-capped, and it runs in a
/// private empty working directory that is deleted afterwards, so no temp file outlives the call. Neither stderr nor stdout is ever
/// logged (they can contain recognised text). The executable path and languages come from configuration only.
/// </summary>
public sealed partial class TesseractCliOcrProvider : IOcrProvider, IDisposable
{
    private const int StderrLimit = 64 * 1024;
    private const int TsvColumns = 12;

    private readonly TesseractOptions _options;
    private readonly DocumentPipelineOptions _pipeline;
    private readonly ILogger<TesseractCliOcrProvider> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate;
    private readonly object _probeLock = new();
    private bool _available;
    private DateTimeOffset _probedAt = DateTimeOffset.MinValue;

    public TesseractCliOcrProvider(
        IOptions<VerificationOptions> options, IOptions<DocumentPipelineOptions> pipeline, ILogger<TesseractCliOcrProvider> logger, TimeProvider clock)
    {
        _options = options.Value.Ocr.Tesseract;
        _pipeline = pipeline.Value;
        _logger = logger;
        _clock = clock;
        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrent));
    }

    public string ProviderId => "tesseract-cli";

    public bool IsAvailable
    {
        get
        {
            lock (_probeLock)
            {
                var now = _clock.GetUtcNow();
                if (now - _probedAt > TimeSpan.FromSeconds(_pipeline.OcrProbeCacheSeconds))
                {
                    _available = Probe();
                    _probedAt = now;
                }

                return _available;
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    public async Task<OcrPage> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new OcrUnavailableException("The OCR engine is not available.");
        }

        var language = ValidLanguage(request.Language) ?? ValidLanguage(_options.Languages)
            ?? throw new OcrUnavailableException("The OCR language configuration is invalid.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tsv = await RunAsync(["stdin", "stdout", "-l", language, "--psm", "6", "tsv"], request.ImagePng, TimeSpan.FromSeconds(_options.TimeoutSecondsPerPage), cancellationToken)
                .ConfigureAwait(false);
            return ParseTsv(tsv, request.PageNumber);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Parses Tesseract TSV (levels: 5 = word). Confidence is scaled to 0..1; words with a negative confidence are layout rows.</summary>
    public static OcrPage ParseTsv(string tsv, int pageNumber)
    {
        var words = new List<(int Block, int Par, int Line, int Left, int Top, int Width, int Height, decimal Conf, string Text)>();
        foreach (var row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = row.TrimEnd('\r').Split('\t', TsvColumns);
            if (c.Length < TsvColumns || c[0] != "5" || string.IsNullOrWhiteSpace(c[11]))
            {
                continue;
            }

            if (int.TryParse(c[2], NumberStyles.None, CultureInfo.InvariantCulture, out var block)
                && int.TryParse(c[3], NumberStyles.None, CultureInfo.InvariantCulture, out var par)
                && int.TryParse(c[4], NumberStyles.None, CultureInfo.InvariantCulture, out var line)
                && int.TryParse(c[6], NumberStyles.None, CultureInfo.InvariantCulture, out var left)
                && int.TryParse(c[7], NumberStyles.None, CultureInfo.InvariantCulture, out var top)
                && int.TryParse(c[8], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                && int.TryParse(c[9], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
                && decimal.TryParse(c[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) && conf >= 0)
            {
                words.Add((block, par, line, left, top, width, height, conf / 100m, c[11].Trim()));
            }
        }

        var lines = words.GroupBy(w => (w.Block, w.Par, w.Line)).OrderBy(g => g.Min(w => w.Top)).ThenBy(g => g.Min(w => w.Left)).Select(g =>
        {
            var ordered = g.OrderBy(w => w.Left).ToList();
            var left = ordered.Min(w => w.Left);
            var top = ordered.Min(w => w.Top);
            return new TextLine(
                string.Join(' ', ordered.Select(w => w.Text)), Math.Round(ordered.Average(w => w.Conf), 4), left, top,
                ordered.Max(w => w.Left + w.Width) - left, ordered.Max(w => w.Top + w.Height) - top);
        }).ToList();

        return new OcrPage(pageNumber, lines, words.Count == 0 ? null : Math.Round(words.Average(w => w.Conf), 4));
    }

    [GeneratedRegex("^[A-Za-z_]{2,16}(\\+[A-Za-z_]{2,16})*$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex LanguagePattern();

    private static string? ValidLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language) && LanguagePattern().IsMatch(language.Trim()) ? language.Trim() : null;

    private ProcessStartInfo StartInfo(IEnumerable<string> args, string workDir)
    {
        var psi = new ProcessStartInfo(_options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["OMP_THREAD_LIMIT"] = "1";
        if (!string.IsNullOrWhiteSpace(_options.TessdataPath))
        {
            psi.Environment["TESSDATA_PREFIX"] = _options.TessdataPath;
        }

        return psi;
    }

    /// <summary>Synchronous on purpose: the availability flag is a property read by health checks. Bounded by the probe timeout, cached.</summary>
    private bool Probe()
    {
        string? dir = null;
        try
        {
            dir = Directory.CreateTempSubdirectory("nv-ocr-probe-").FullName;
            using var process = Process.Start(StartInfo(["--list-langs"], dir)) ?? throw new OcrUnavailableException("The OCR engine could not be started.");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(_pipeline.OcrProbeTimeoutSeconds)))
            {
                Kill(process);
                return false;
            }

            process.WaitForExit();
            var output = stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty;
            output += "\n" + (stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty); // older versions print the list to stderr
            var installed = output.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wanted = _options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ok = process.ExitCode == 0 && wanted.Length > 0 && wanted.All(installed.Contains);
            if (!ok)
            {
                _logger.LogWarning("OCR engine is present but not usable (missing language pack or failure)");
            }

            return ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("OCR engine is not available ({ExceptionType})", ex.GetType().Name);
            return false;
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private async Task<string> RunAsync(string[] args, byte[]? stdin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var work = Directory.CreateTempSubdirectory("nv-ocr-").FullName;
        Process? process = null;
        try
        {
            process = Process.Start(StartInfo(args, work)) ?? throw new OcrUnavailableException("The OCR engine could not be started.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var stdout = ReadCappedAsync(process.StandardOutput.BaseStream, _pipeline.MaxOcrOutputBytes, cts.Token);
            var stderr = ReadCappedAsync(process.StandardError.BaseStream, StderrLimit, cts.Token);
            try
            {
                if (stdin is not null)
                {
                    await process.StandardInput.BaseStream.WriteAsync(stdin, cts.Token).ConfigureAwait(false);
                }

                process.StandardInput.Close();
                var text = await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new OcrUnavailableException($"The OCR engine failed (exit code {process.ExitCode}).");
                }

                return text;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OcrUnavailableException("The OCR engine timed out.");
            }
            catch (IOException ex)
            {
                throw new OcrUnavailableException("The OCR engine closed its input early.", ex);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new OcrUnavailableException("The OCR engine is not installed.", ex);
        }
        finally
        {
            Kill(process);
            process?.Dispose();
            TryDelete(work);
        }
    }

    private static async Task<string> ReadCappedAsync(Stream stream, int limit, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > limit)
            {
                throw new OcrUnavailableException("The OCR engine produced more output than allowed.");
            }

            ms.Write(buffer, 0, n);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static void Kill(Process? p)
    {
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already gone
        }
    }

    private static void TryDelete(string? dir)
    {
        try
        {
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort; the directory is empty or owned by a process that is being killed
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
