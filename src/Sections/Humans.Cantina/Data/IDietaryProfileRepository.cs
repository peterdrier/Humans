using Humans.Base.Interfaces.Repositories;
using Humans.Cantina.Domain;
using NodaTime;

namespace Humans.Cantina.Data;

internal interface IDietaryProfileRepository : IRepository
{
    Task<DietaryProfile?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<DietaryProfile>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>For the data migration's pending list.</summary>
    Task<IReadOnlyList<DietaryProfile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Overwrites every food field and the decline flag.</summary>
    Task UpsertAsync(DietaryProfile values, CancellationToken ct = default);

    /// <summary>False when the member already had a row and nothing was written.</summary>
    Task<bool> TryInsertAsync(DietaryProfile values, CancellationToken ct = default);

    /// <summary>False when there was none.</summary>
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Moved when the target has none, deleted when it has its own (the survivor's answers win). Idempotent.</summary>
    Task ReassignAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default);
}
