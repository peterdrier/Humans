using Humans.Base.Interfaces.Repositories;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

/// <summary>
/// The only non-test reader and writer of <c>consent_reviews</c>.
/// </summary>
internal interface IConsentReviewRepository : IRepository
{
    Task<ConsentReview?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<ConsentReview>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConsentReview>> GetByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Inserts or overwrites the member's row.</summary>
    Task UpsertAsync(ConsentReview values, CancellationToken ct = default);

    /// <summary>Inserts only when the member has no row; false when one exists.</summary>
    Task<bool> TryInsertAsync(ConsentReview values, CancellationToken ct = default);

    /// <summary>Article 17: nulls the free-text notes and rejection reason on the member's row.</summary>
    Task EraseFreeTextAsync(Guid userId, CancellationToken ct = default);
}
