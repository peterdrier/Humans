using Humans.Base.Interfaces.Repositories;
using Humans.Grants.Domain;
using NodaTime;

namespace Humans.Grants.Data;

/// <summary>
/// Every row in the section that carries one person's id: the GDPR export, erasure and merge
/// surface. <see cref="Grants"/> are the person's own; the rest is what they did as an actor.
/// </summary>
internal sealed record GrantUserRows(
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<GrantProgram> ProgramsCreated,
    IReadOnlyList<GrantProgramManager> ManagerRows,
    IReadOnlyList<GrantProgramManager> ManagersAdded,
    IReadOnlyList<Grant> GrantsIssued,
    IReadOnlyList<Grant> GrantsRevoked);

/// <summary>
/// Data-access interface for the Grants section: the only caller of <c>GrantsDbContext</c>.
/// Registered Singleton over <c>IDbContextFactory</c>; every method opens its own short-lived
/// context, reads are no-tracking, and the single-row getters return detached entities the
/// service mutates and hands back to an <c>Update…</c> call.
/// </summary>
internal interface IGrantRepository : IRepository
{
    /// <summary>Every program with its managers and grants. A handful of rows at this scale.</summary>
    Task<IReadOnlyList<GrantProgram>> GetProgramsAsync(CancellationToken ct = default);

    /// <summary>One program with its managers and grants, or null.</summary>
    Task<GrantProgram?> GetProgramAsync(Guid id, CancellationToken ct = default);

    /// <summary>One grant with its program and the program's managers, or null.</summary>
    Task<Grant?> GetGrantAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every grant, any state, held by one of <paramref name="userIds"/>, with its program.</summary>
    Task<IReadOnlyList<Grant>> GetGrantsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task AddProgramAsync(GrantProgram program, CancellationToken ct = default);

    /// <summary>Saves the program's scalar columns only; managers go through <see cref="SetManagersAsync"/>.</summary>
    Task UpdateProgramAsync(GrantProgram program, CancellationToken ct = default);

    /// <summary>Adds <paramref name="toAdd"/> and deletes the rows for <paramref name="toRemove"/> in one save.</summary>
    Task SetManagersAsync(
        Guid programId, IReadOnlyList<GrantProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
        CancellationToken ct = default);

    /// <summary>Inserts every grant in one save: all or nothing.</summary>
    Task AddGrantsAsync(IReadOnlyList<Grant> grants, CancellationToken ct = default);

    Task UpdateGrantAsync(Grant grant, CancellationToken ct = default);

    /// <summary>True when one of <paramref name="userIds"/> manages any program.</summary>
    Task<bool> ManagesAnyProgramAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    // ── GDPR ──────────────────────────────────────────────────────────────

    Task<GrantUserRows> GetRowsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Article 17: deletes the person's grants and manager rows.</summary>
    Task EraseUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Account merge: re-keys grant, manager and actor columns from <paramref name="sourceUserId"/>
    /// to <paramref name="targetUserId"/>. Where both hold a live grant in one program, the newer
    /// one is revoked first so the survivor keeps exactly one. Returns the ids revoked. Idempotent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant now, CancellationToken ct = default);
}
