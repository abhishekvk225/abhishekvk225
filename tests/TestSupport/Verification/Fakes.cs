using System.Collections.Concurrent;
using System.Security.Cryptography;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.TestSupport.Verification;

/// <summary>Deterministic OCR: canned pages keyed by the SHA-256 (hex) of the image, with a default for anything else. Test use only.</summary>
public sealed class FakeOcrProvider : IOcrProvider
{
    private readonly ConcurrentDictionary<string, OcrPage> _byHash = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderId => "fake-ocr";

    public bool IsAvailable { get; set; } = true;

    public OcrPage? Default { get; set; }

    public int Calls { get; private set; }

    public static string HashOf(byte[] image) => Convert.ToHexString(SHA256.HashData(image));

    public FakeOcrProvider Returns(byte[] image, params string[] lines)
    {
        _byHash[HashOf(image)] = Page(lines);
        return this;
    }

    public FakeOcrProvider ReturnsByDefault(decimal? confidence, params string[] lines)
    {
        Default = Page(lines, confidence);
        return this;
    }

    public static OcrPage Page(IEnumerable<string> lines, decimal? confidence = 0.95m) =>
        new(1, lines.Select(l => new TextLine(l, confidence, null, null, null, null)).ToList(), confidence);

    public Task<OcrPage> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        if (!IsAvailable)
        {
            throw new OcrUnavailableException("fake OCR is unavailable");
        }

        return Task.FromResult(_byHash.TryGetValue(HashOf(request.ImagePng), out var page) ? page : Default ?? new OcrPage(request.PageNumber, [], null));
    }
}

public sealed class FakePdfTextExtractor : IPdfTextExtractor
{
    public List<PageText> Pages { get; set; } = [];

    public bool Throws { get; set; }

    public Task<IReadOnlyList<PageText>> ExtractAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken cancellationToken) =>
        Throws ? throw new InvalidDataException("fake corrupt pdf") : Task.FromResult<IReadOnlyList<PageText>>(Pages.Take(maxPages).ToList());
}

public sealed class FakePdfRasterizer : IPdfPageRasterizer
{
    public byte[] Png { get; set; } = SampleDocuments.Png(64, 64);

    public List<int> RenderedPages { get; } = [];

    public Task<IReadOnlyList<byte[]>> RenderPagesAsync(ReadOnlyMemory<byte> pdf, int maxPages, int dpi, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<byte[]>>(Enumerable.Range(1, maxPages).Select(_ => Png).ToList());

    public Task<byte[]> RenderPageAsync(ReadOnlyMemory<byte> pdf, int pageNumber, int dpi, CancellationToken cancellationToken)
    {
        RenderedPages.Add(pageNumber);
        return Task.FromResult(Png);
    }
}

public sealed class PassThroughImagePreprocessor : IDocumentImagePreprocessor
{
    public Task<Result<byte[]>> PrepareForOcrAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken) =>
        Task.FromResult(Result<byte[]>.Success(image.ToArray()));
}

/// <summary>Returns canned text for any document (processor tests need no PDF/OCR at all).</summary>
public sealed class FakeDocumentParser : IDocumentParser
{
    public Result<DocumentText> Result { get; set; } = Result<DocumentText>.Failure(new Error("DOCUMENT_UNREADABLE", "fake", ErrorType.UnprocessableEntity));

    public static FakeDocumentParser WithLines(IEnumerable<string> lines, decimal? confidence = null, TextSource source = TextSource.DigitalPdf) =>
        new() { Result = SampleDocuments.Text(lines, confidence, source) };

    public Task<Result<DocumentText>> ParseAsync(ReadOnlyMemory<byte> content, DocumentKind kind, ParseOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(Result);
}

public enum FakeProviderMode
{
    ByNumberSuffix = 0,
    Verified,
    NotVerified,
    Inconclusive,
    Error,
    Timeout,
    Throws,
    VerifiedWithoutReference,
}

/// <summary>Scripted PAN provider. By default the LAST character of the number decides: V verified, N not verified, I inconclusive, E error, T timeout.</summary>
public sealed class FakePanVerificationProvider : IPanVerificationProvider
{
    public string ProviderId => "fake-pan";

    public bool IsConfigured { get; set; } = true;

    public FakeProviderMode Mode { get; set; } = FakeProviderMode.ByNumberSuffix;

    public int Calls { get; private set; }

    public PanProviderRequest? LastRequest { get; private set; }

    public async Task<ProviderResult> VerifyAsync(PanProviderRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        return await FakeProviderScript.RunAsync(Mode, request.Pan, cancellationToken);
    }
}

public sealed class FakePassportVerificationProvider : IPassportVerificationProvider
{
    public string ProviderId => "fake-passport";

    public bool IsConfigured { get; set; } = true;

    public FakeProviderMode Mode { get; set; } = FakeProviderMode.ByNumberSuffix;

    public int Calls { get; private set; }

    public async Task<ProviderResult> VerifyAsync(PassportProviderRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return await FakeProviderScript.RunAsync(Mode, request.PassportNumber, cancellationToken);
    }
}

internal static class FakeProviderScript
{
    public static async Task<ProviderResult> RunAsync(FakeProviderMode mode, string number, CancellationToken ct)
    {
        if (mode == FakeProviderMode.ByNumberSuffix)
        {
            mode = (number.Length == 0 ? ' ' : char.ToUpperInvariant(number[^1])) switch
            {
                'V' => FakeProviderMode.Verified,
                'N' => FakeProviderMode.NotVerified,
                'I' => FakeProviderMode.Inconclusive,
                'E' => FakeProviderMode.Error,
                'T' => FakeProviderMode.Timeout,
                _ => FakeProviderMode.Inconclusive,
            };
        }

        switch (mode)
        {
            case FakeProviderMode.Verified:
                return new ProviderResult(ExternalState.Verified, "fake-ref-1", [new FieldMatch("name", FieldMatchOutcome.Match)], null);
            case FakeProviderMode.VerifiedWithoutReference:
                return new ProviderResult(ExternalState.Verified, null, [], null);
            case FakeProviderMode.NotVerified:
                return new ProviderResult(ExternalState.NotVerified, "fake-ref-2", [new FieldMatch("name", FieldMatchOutcome.Mismatch)], "NO_MATCH");
            case FakeProviderMode.Error:
                return new ProviderResult(ExternalState.ProviderError, null, [], "UPSTREAM_5XX");
            case FakeProviderMode.Timeout:
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // honours the guard's timeout token
                return new ProviderResult(ExternalState.Timeout, null, [], null);
            case FakeProviderMode.Throws:
                throw new HttpRequestException("fake network failure containing sensitive-canary");
            default:
                return new ProviderResult(ExternalState.Inconclusive, null, [], "AMBIGUOUS");
        }
    }
}
