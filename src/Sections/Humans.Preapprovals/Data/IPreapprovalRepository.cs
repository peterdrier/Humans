using Humans.Base.Interfaces.Repositories;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Data;

/// <summary>
/// Every row carrying one person's id: the GDPR export, erasure and merge surface. <see cref="Grants"/> are the
/// person's own; the rest is what they did as an actor.
/// </summary>
internal sealed record PreapprovalUserRows(
    IReadOnlyList<PreapprovalGrant> Grants,
    IReadOnlyList<PreapprovalProgram> ProgramsCreated,
    IReadOnlyList<ProgramManager> ManagersAdded,
    IReadOnlyList<ProgramManager> ManagerRows,
    IReadOnlyList<PreapprovalGrant> GrantsDecided);

/// <summary>
/// Single-row getters return detached entities the service mutates and hands back to an <c>Update…</c> call. Writes
/// inside a caller's ambient <c>TransactionScope</c> enlist in it.
/// </summary>
internal interface IPreapprovalRepository : IRepository
{
    /// <summary>With managers and grants: a handful of rows at this scale.</summary>
    Task<IReadOnlyList<PreapprovalProgram>> GetProgramsAsync(CancellationToken ct = default);

    /// <summary>With managers and grants.</summary>
    Task<PreapprovalProgram?> GetProgramAsync(Guid id, CancellationToken ct = default);

    /// <summary>With its program and the program's managers.</summary>
    Task<PreapprovalGrant?> GetGrantAsync(Guid id, CancellationToken ct = default);

    /// <summary>With their programs.</summary>
    Task<IReadOnlyList<PreapprovalGrant>> GetGrantsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>With their programs.</summary>
    Task<IReadOnlyList<PreapprovalGrant>> GetGrantsByStatusAsync(GrantStatus status, CancellationToken ct = default);

    Task AddProgramAsync(PreapprovalProgram program, CancellationToken ct = default);

    /// <summary>Scalar columns only; managers go through <see cref="SetManagersAsync"/>.</summary>
    Task UpdateProgramAsync(PreapprovalProgram program, CancellationToken ct = default);

    /// <summary>One save.</summary>
    Task SetManagersAsync(
        Guid programId, IReadOnlyList<ProgramManager> toAdd, IReadOnlyList<Guid> toRemove,
        CancellationToken ct = default);

    /// <summary>One save: all or nothing.</summary>
    Task AddGrantsAsync(IReadOnlyList<PreapprovalGrant> grants, CancellationToken ct = default);

    Task UpdateGrantAsync(PreapprovalGrant grant, CancellationToken ct = default);

    /// <summary>Any deadline.</summary>
    Task<IReadOnlyList<ReminderSent>> GetRemindersSentAsync(
        IReadOnlyCollection<Guid> grantIds, CancellationToken ct = default);

    Task AddReminderSentAsync(ReminderSent reminder, CancellationToken ct = default);

    Task<bool> HasGrantsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<bool> ManagesAnyProgramAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>An open program accepting applications whose deadline is on or after <paramref name="today"/>.</summary>
    Task<bool> AnyProgramAcceptsApplicationsAsync(LocalDate today, CancellationToken ct = default);

    Task<PreapprovalUserRows> GetRowsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Article 17: deletes the person's Applied rows and manager rows. Decided grants are retained in full.</summary>
    Task EraseUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// A duplicate manager row collapses to one; two Applied rows for the same program collapse to the older one.
    /// Returns the ids of the dropped applications. Idempotent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default);
}
