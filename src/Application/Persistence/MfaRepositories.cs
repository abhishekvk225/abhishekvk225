using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Persistence;

public interface IMfaRepository
{
    Task<UserMfa?> GetAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserMfa mfa);

    void Remove(UserMfa mfa);

    Task<int> CountUnusedCodesAsync(Guid userId, CancellationToken cancellationToken);

    Task<MfaRecoveryCode?> FindUnusedCodeAsync(Guid userId, byte[] codeHash, CancellationToken cancellationToken);

    /// <summary>Every recovery code of the user (used and unused), tracked, so a reset or regeneration can remove them.</summary>
    Task<IReadOnlyList<MfaRecoveryCode>> GetCodesAsync(Guid userId, CancellationToken cancellationToken);

    void Add(MfaRecoveryCode code);

    void Remove(MfaRecoveryCode code);

    Task<MfaChallenge?> FindChallengeAsync(byte[] tokenHash, CancellationToken cancellationToken);

    void Add(MfaChallenge challenge);
}

/// <summary>
/// The MFA operations that must be single atomic statements: attempt counting, single-use consumption and replay protection.
/// Each returns whether THIS caller won, so concurrent requests can never both succeed.
/// </summary>
public interface IMfaAtomics
{
    /// <summary>Counts one attempt against a live challenge. False when the challenge is unknown, used, expired or out of attempts.</summary>
    Task<bool> TryRegisterAttemptAsync(Guid challengeId, int maxAttempts, DateTime now, CancellationToken cancellationToken);

    /// <summary>Marks the challenge used. Exactly one caller gets true.</summary>
    Task<bool> TryConsumeChallengeAsync(Guid challengeId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Records that a time step was used. False when that step (or a newer one) was already accepted: a replayed code.</summary>
    Task<bool> TryAdvanceStepAsync(Guid userId, long step, CancellationToken cancellationToken);

    /// <summary>Marks a recovery code used. Exactly one caller gets true.</summary>
    Task<bool> TryUseRecoveryCodeAsync(Guid codeId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Removes the user's expired or used challenges (housekeeping on issue, keeps the table bounded).</summary>
    Task PurgeChallengesAsync(Guid userId, DateTime now, CancellationToken cancellationToken);
}
