using Humans.Base.Interfaces;

namespace Humans.Gdpr.Contracts;

/// <summary>
/// Implemented by every service that owns user-scoped tables. Reads only its own section's tables;
/// other sections' data arrives through their own contributors.
/// </summary>
public interface IUserDataContributor : IFanout
{
    /// <summary>
    /// Read-only. Receives the surviving account id. Every section name returned must be a key of
    /// <see cref="ErasureDeclaration"/> (the orchestrator logs an error otherwise); a <c>null</c>
    /// <see cref="UserDataSlice.Data"/> is dropped.
    /// </summary>
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Called right after <see cref="ContributeForUserAsync"/> with the survivor's whole chain
    /// (<paramref name="userId"/> first, then archived ids), for a contributor that can't resolve the chain
    /// itself. Same rules; a section name returned by both methods fails the export.
    /// </summary>
    Task<IReadOnlyList<UserDataSlice>> ContributeForIdentityChainAsync(
        Guid userId, IReadOnlyList<Guid> allUserIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

    /// <summary>
    /// Section name → <c>null</c> when erased in full, or the reason (and lawful basis) for what is kept.
    /// Covers every export section name, plus any category erased without being exported.
    /// Must be static: the erasure-coverage test reads it from an uninitialized instance.
    /// </summary>
    IReadOnlyDictionary<string, string?> ErasureDeclaration { get; }

    /// <summary>Must be idempotent: the deletion job retries the whole cascade the next day after a failure.</summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// True only for the identity owner, which erases after everyone else so sections that still need the
    /// person's address (a Workspace suspend) can resolve it.
    /// </summary>
    bool ErasesLast => false;
}
