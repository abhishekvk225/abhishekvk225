using NexaVerify.Application.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Abstractions;

/// <summary>
/// Submission of one verification type. The shared core runs, in order and BEFORE anything is stored or charged: client principal,
/// module gate, field validation, idempotency replay, queue cap, credit pre-flight, document inspection; then stores the encrypted
/// document and inserts the request (Queued) in one transaction. No credit is deducted at this point.
/// </summary>
public interface IDocumentVerificationService<in TRequest>
{
    VerificationType Type { get; }

    Task<Result<VerificationAcceptedDto>> SubmitAsync(TRequest request, DocumentUpload? document, string? idempotencyKey, CancellationToken cancellationToken);
}

public interface IBankStatementVerificationService : IDocumentVerificationService<SubmitBankStatementRequest>
{
}

public interface IPanVerificationService : IDocumentVerificationService<SubmitPanRequest>
{
}

public interface IPassportVerificationService : IDocumentVerificationService<SubmitPassportRequest>
{
}
