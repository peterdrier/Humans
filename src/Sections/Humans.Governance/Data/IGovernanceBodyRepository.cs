using Humans.Base.Interfaces.Repositories;
using Humans.Governance.Domain;

namespace Humans.Governance.Data;

internal interface IGovernanceBodyRepository : IRepository
{
    /// <summary>Every stored body with its files; a body with no row is absent.</summary>
    Task<IReadOnlyList<GovernanceBodyLinks>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or overwrites the body's row and replaces its files.</summary>
    Task ReplaceAsync(GovernanceBodyLinks links, CancellationToken ct = default);
}
