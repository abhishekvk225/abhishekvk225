using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Infrastructure.Verification.Documents;
using NexaVerify.Infrastructure.Verification.Ocr;
using NexaVerify.Infrastructure.Verification.Providers;

namespace NexaVerify.Infrastructure.Verification;

public static class VerificationProcessingRegistration
{
    /// <summary>
    /// Document processing (Backend-B): inspector, PDF text, rasteriser, image preprocessor, OCR provider, document parser, the
    /// NotConfigured providers and their resolvers, the pure module logic and the three <c>IModuleProcessor</c>s.
    /// Requires <c>AddVerificationInfrastructure</c> (binds <c>VerificationOptions</c>) and ordinary logging. Registers no
    /// <c>IVerificationModule</c>, no storage, no persistence and no hosted service.
    /// </summary>
    public static IServiceCollection AddVerificationProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DocumentPipelineOptions>()
            .Bind(configuration.GetSection(DocumentPipelineOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DocumentPipelineOptions>, DocumentPipelineOptionsValidator>());

        services.TryAddSingleton<IDocumentInspector, DocumentInspector>();
        services.TryAddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.TryAddSingleton<IDocumentImagePreprocessor, SkiaDocumentImagePreprocessor>();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<IPdfRasterizer, PdfiumRasterizer>();
        }
        else
        {
            services.TryAddSingleton<IPdfRasterizer, UnavailablePdfRasterizer>();
        }

        services.TryAddSingleton<IOcrProvider>(sp =>
            string.Equals(sp.GetRequiredService<IOptions<VerificationOptions>>().Value.Ocr.Provider, "none", StringComparison.OrdinalIgnoreCase)
                ? new NullOcrProvider()
                : ActivatorUtilities.CreateInstance<TesseractCliOcrProvider>(sp));
        services.TryAddSingleton<IDocumentParser, DocumentParser>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPanVerificationProvider, NotConfiguredPanVerificationProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPassportVerificationProvider, NotConfiguredPassportVerificationProvider>());
        services.TryAddSingleton<IPanVerificationProviderResolver, PanVerificationProviderResolver>();
        services.TryAddSingleton<IPassportVerificationProviderResolver, PassportVerificationProviderResolver>();

        return services.AddVerificationModuleLogic();
    }
}
