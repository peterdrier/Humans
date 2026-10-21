using Humans.Base.Interfaces.Repositories;
using Humans.Cantina.Domain;
using NodaTime;

namespace Humans.Cantina.Data;

/// <summary>The sole reader and writer of <c>cantina_dietary_profiles</c>.</summary>
internal interface IDietaryProfileRepository : IRepository
{
    Task<DietaryProfile?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<DietaryProfile>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Inserts the member's row or overwrites every food field on it.</summary>
    Task UpsertAsync(DietaryProfile values, CancellationToken ct = default);

    /// <summary>Deletes the member's row. False when there was none.</summary>
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Folds the source's row into the target: moved when the target has none, deleted when
    /// the target has its own (the survivor's answers win). Idempotent.</summary>
    Task ReassignAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default);
}
