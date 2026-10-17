using Humans.Base.Interfaces.Repositories;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Data;

/// <summary>
/// Every row in the section that carries one person's id — the GDPR export, erasure and merge
/// surface. <see cref="Grants"/> are the person's own; the rest is what they did as an actor.
/// </summary>
internal sealed record PreapprovalUserRows(
    IReadOnlyList<PreapprovalGrant> Grants,
    IReadOnlyList<PreapprovalProgram> ProgramsCreated,
    IReadOnlyList<ProgramManager> ManagersAdded,
    IReadOnlyList<ProgramManager> ManagerRows,
    IReadOnlyList<PreapprovalGrant> GrantsDecided);

/// <summary>
/// Data-access interface for the Preapprovals section: the only caller of
/// <c>PreapprovalsDbContext</c>. Registered Singleton over <c>IDbContextFactory</c>; every method
/// opens its own short-lived context, reads are no-tracking, and the single-row getters return
/// detached entities the service mutates and hands back to an <c>Update…</c> call. Writes made
/// inside a caller's ambient <c>TransactionScope</c> enlist in it.
/// </summary>
internal interface IPreapprovalRepository : IRepository
{
    /// <summary>Every program with its managers and grants. A handful of rows at this scale.</summary>
    Task<IReadOnlyList<PreapprovalProgram>> GetProgramsAsync(CancellationToken ct = default);

    /// <summary>One program with its managers and grants, or null.</summary>
    Task<PreapprovalProgram?> GetProgramAsync(Guid id, CancellationToken ct = default);

    /// <summary>One grant with its program and the program's managers, or null.</summary>
    Task<PreapprovalGrant?> GetGrantAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every grant whose beneficiary is one of <paramref name="userIds"/>, with its program.</summary>
    Task<IReadOnlyList<PreapprovalGrant>> GetGrantsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Every grant in <paramref name="status"/>, with its program.</summary>
    Task<IReadOnlyList<PreapprovalGrant>> GetGrantsByStatusAsync(GrantStatus status, CancellationToken ct = default);

    Task AddProgramAsync(PreapprovalProgram program, CancellationToken ct = default);

    /// <summary>Saves the program's scalar columns only; managers go through <see cref="SetManagersAsync"/>.</summary>
    Task UpdateProgramAsync(PreapprovalProgram program, CancellationToken ct = default);

    /// <summary>Adds <paramref name="toAdd"/> and deletes the rows for <paramref name="toRemove"/> in one save.</summary>
    Task SetManagersAsync(
        Guid programId, IReadOnlyList<ProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
        CancellationToken ct = default);

    /// <summary>Inserts every grant in one save: all or nothing.</summary>
    Task AddGrantsAsync(IReadOnlyList<PreapprovalGrant> grants, CancellationToken ct = default);

    Task UpdateGrantAsync(PreapprovalGrant grant, CancellationToken ct = default);

    /// <summary>The reminders already sent for <paramref name="grantIds"/>, any deadline.</summary>
    Task<IReadOnlyList<ReminderSent>> GetRemindersSentAsync(
        IReadOnlyCollection<Guid> grantIds, CancellationToken ct = default);

    Task AddReminderSentAsync(ReminderSent reminder, CancellationToken ct = default);

    /// <summary>True when any grant belongs to one of <paramref name="userIds"/>.</summary>
    Task<bool> HasGrantsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>True when one of <paramref name="userIds"/> manages any program.</summary>
    Task<bool> ManagesAnyProgramAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>True when an open program accepts applications and its deadline is on or after <paramref name="today"/>.</summary>
    Task<bool> AnyProgramAcceptsApplicationsAsync(LocalDate today, CancellationToken ct = default);

    // ── GDPR ──────────────────────────────────────────────────────────────

    Task<PreapprovalUserRows> GetRowsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Article 17: deletes what was never decided — the person's <see cref="GrantStatus.Applied"/>
    /// rows and their manager rows. Decided grants are retained in full.
    /// </summary>
    Task EraseUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Account merge: re-keys beneficiary, manager and actor columns from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>. A duplicate manager row
    /// collapses to one; two <see cref="GrantStatus.Applied"/> rows for the same program collapse
    /// to the older one. Returns the ids of the applications dropped. Idempotent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default);
}
