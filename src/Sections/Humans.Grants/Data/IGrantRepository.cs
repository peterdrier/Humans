using Humans.Base.Interfaces.Repositories;
using Humans.Grants.Domain;
using NodaTime;

namespace Humans.Grants.Data;

/// <summary><see cref="Grants"/> are the person's own; the rest is what they did as an actor.</summary>
internal sealed record GrantUserRows(
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<GrantProgram> ProgramsCreated,
    IReadOnlyList<GrantProgramManager> ManagerRows,
    IReadOnlyList<GrantProgramManager> ManagersAdded,
    IReadOnlyList<Grant> GrantsIssued,
    IReadOnlyList<Grant> GrantsRevoked);

/// <summary>Single-row getters return detached entities the service mutates and hands back to an <c>Update…</c> call.</summary>
internal interface IGrantRepository : IRepository
{
    /// <summary>With managers and grants.</summary>
    Task<IReadOnlyList<GrantProgram>> GetProgramsAsync(CancellationToken ct = default);

    /// <summary>With managers and grants; null when missing.</summary>
    Task<GrantProgram?> GetProgramAsync(Guid id, CancellationToken ct = default);

    /// <summary>With its program and the program's managers; null when missing.</summary>
    Task<Grant?> GetGrantAsync(Guid id, CancellationToken ct = default);

    /// <summary>Any state, with the program.</summary>
    Task<IReadOnlyList<Grant>> GetGrantsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task AddProgramAsync(GrantProgram program, CancellationToken ct = default);

    /// <summary>Program fields and manager changes in one save.</summary>
    Task UpdateProgramAsync(
        GrantProgram program, IReadOnlyList<GrantProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
        CancellationToken ct = default);

    /// <summary>Stamps each listed program that has no event id yet; returns the rows written.</summary>
    Task<int> AssignEventIdsAsync(IReadOnlyDictionary<Guid, Guid> eventByProgram, CancellationToken ct = default);

    /// <summary>One save: all or nothing.</summary>
    Task AddGrantsAsync(IReadOnlyList<Grant> grants, CancellationToken ct = default);

    Task UpdateGrantAsync(Grant grant, CancellationToken ct = default);

    Task<bool> ManagesAnyProgramAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<GrantUserRows> GetRowsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task EraseUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Re-keys grant, manager and actor columns. Where both hold a live grant in one program, the newer is revoked so
    /// the survivor keeps one. Revocation and re-keying commit in one transaction. Returns the ids revoked. Idempotent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant now, CancellationToken ct = default);
}
