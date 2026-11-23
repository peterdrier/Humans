using Humans.Base.Interfaces.Repositories;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Data;

internal interface IGovernanceBodyRepository : IRepository
{
    /// <summary>Every stored body with its files; a body with no row is absent.</summary>
    Task<IReadOnlyList<GovernanceBodyLinks>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or overwrites the body's row and replaces its files.</summary>
    Task ReplaceAsync(GovernanceBodyLinks links, CancellationToken ct = default);

    Task<IReadOnlyList<GovernanceBoardPosition>> GetBoardPositionsAsync(CancellationToken ct = default);

    /// <summary>Gives <paramref name="position"/> to <paramref name="userId"/>, dropping any other office they held;
    /// a null user leaves the office empty.</summary>
    Task SetBoardPositionAsync(BoardPosition position, Guid? userId, Instant now, CancellationToken ct = default);

    /// <summary>Drops whatever office the user holds.</summary>
    Task ClearBoardPositionForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Moves the merged account's office to the survivor unless the survivor already holds one.</summary>
    Task ReassignBoardPositionAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default);
}
