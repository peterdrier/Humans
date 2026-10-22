using Humans.Base.Interfaces.Repositories;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

internal interface IConsentReviewRepository : IRepository
{
    Task<ConsentReview?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<ConsentReview>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConsentReview>> GetByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task UpsertAsync(ConsentReview values, CancellationToken ct = default);

    /// <summary>False when the member already has a row.</summary>
    Task<bool> TryInsertAsync(ConsentReview values, CancellationToken ct = default);

    /// <summary>Article 17: nulls the notes and rejection reason, keeping the rest.</summary>
    Task EraseFreeTextAsync(Guid userId, CancellationToken ct = default);
}
