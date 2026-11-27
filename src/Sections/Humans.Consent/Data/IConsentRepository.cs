using Humans.Base.Interfaces.Repositories;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

/// <summary>
/// Append-only (design-rules.md, *Append-only entities*): DB triggers also reject UPDATE and DELETE. Reads take a merge chain's id set,
/// so the survivor's view includes records still attributed to merged-away ids.
/// </summary>
internal interface IConsentRepository : IRepository
{
    Task AddAsync(ConsentRecord record, CancellationToken ct = default);

    Task<bool> ExistsForVersionAsync(
        IReadOnlyCollection<Guid> userIds, Guid documentVersionId, CancellationToken ct = default);

    /// <summary>The most recent match when several ids signed the same version; null if none.</summary>
    Task<ConsentRecord?> GetForVersionAsync(
        IReadOnlyCollection<Guid> userIds, Guid documentVersionId, CancellationToken ct = default);

    /// <summary>Newest first, with the version and its document included.</summary>
    Task<IReadOnlyList<ConsentRecord>> GetAllForUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<int> GetCountForUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Explicit consents only.</summary>
    Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Explicit consents only. Every input user id appears, with an empty set when it has none.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentedVersionIdsByUserAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

}
