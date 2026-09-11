using Humans.Base.Interfaces.Repositories;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Data;

/// <summary>
/// The whole register in one read: every group with its members, meetings, log entries
/// and documents (each with its comments). A few dozen rows at this scale, so the
/// service works over it in memory rather than issuing a query per question.
/// </summary>
internal sealed record WorkgroupsGraph(IReadOnlyList<Workgroup> Workgroups);

/// <summary>
/// Every row in the section that carries one person's attribution — the GDPR export and
/// erasure surface. Group names are stitched by the service from the register graph.
/// </summary>
internal sealed record WorkgroupUserRows(
    IReadOnlyList<WorkgroupMember> Memberships,
    IReadOnlyList<WorkgroupLogEntry> LogEntries,
    IReadOnlyList<WorkgroupMeeting> Meetings,
    IReadOnlyList<WorkgroupDocument> Documents,
    IReadOnlyList<WorkgroupDocumentComment> Comments);

/// <summary>
/// Data-access interface for the Workgroups section. Owns the six <c>workgroup*</c>
/// tables and is the only caller of <c>WorkgroupsDbContext</c>. Implementation uses
/// <c>IDbContextFactory&lt;WorkgroupsDbContext&gt;</c> so the repository can be
/// registered Singleton — every method opens its own short-lived context; reads are
/// no-tracking and the single-row getters return detached entities the service mutates
/// and hands back to an <c>Update…</c> call.
/// </summary>
internal interface IWorkgroupRepository : IRepository
{
    // ── Register graph ────────────────────────────────────────────────────

    /// <summary>The whole register, detached, with every child collection loaded.</summary>
    Task<WorkgroupsGraph> GetGraphAsync(CancellationToken ct = default);

    // ── Single rows for mutation ──────────────────────────────────────────

    /// <summary>One group with members, meetings, log entries and documents loaded.</summary>
    Task<Workgroup?> GetWorkgroupAsync(Guid id, CancellationToken ct = default);

    /// <summary>As <see cref="GetWorkgroupAsync"/>, by slug.</summary>
    Task<Workgroup?> GetWorkgroupBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>True when another group already holds <paramref name="slug"/>.</summary>
    Task<bool> SlugTakenAsync(string slug, Guid? exceptId = null, CancellationToken ct = default);

    Task<WorkgroupMember?> GetMemberAsync(Guid id, CancellationToken ct = default);

    Task<WorkgroupMeeting?> GetMeetingAsync(Guid id, CancellationToken ct = default);

    Task<WorkgroupLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>Document with its comments loaded.</summary>
    Task<WorkgroupDocument?> GetDocumentAsync(Guid id, CancellationToken ct = default);

    /// <summary>Comment with its document loaded (the window and status rules need it).</summary>
    Task<WorkgroupDocumentComment?> GetCommentAsync(Guid id, CancellationToken ct = default);

    // ── Writes ────────────────────────────────────────────────────────────

    /// <summary>Inserts a group together with the rows created in the same act (members, the log entry).</summary>
    Task AddWorkgroupAsync(Workgroup workgroup, CancellationToken ct = default);

    Task UpdateWorkgroupAsync(Workgroup workgroup, CancellationToken ct = default);

    Task AddMemberAsync(WorkgroupMember member, CancellationToken ct = default);

    Task UpdateMembersAsync(IReadOnlyList<WorkgroupMember> members, CancellationToken ct = default);

    Task AddMeetingAsync(WorkgroupMeeting meeting, CancellationToken ct = default);

    Task UpdateMeetingAsync(WorkgroupMeeting meeting, CancellationToken ct = default);

    Task AddLogEntryAsync(WorkgroupLogEntry entry, CancellationToken ct = default);

    Task UpdateLogEntryAsync(WorkgroupLogEntry entry, CancellationToken ct = default);

    /// <summary>Hard-deletes a member log entry. System entries are never deleted.</summary>
    Task DeleteLogEntryAsync(Guid id, CancellationToken ct = default);

    Task AddDocumentAsync(WorkgroupDocument document, CancellationToken ct = default);

    Task UpdateDocumentAsync(WorkgroupDocument document, CancellationToken ct = default);

    Task AddCommentAsync(WorkgroupDocumentComment comment, CancellationToken ct = default);

    /// <summary>Saves one or many comments — the per-comment response and the bulk category pass.</summary>
    Task UpdateCommentsAsync(IReadOnlyList<WorkgroupDocumentComment> comments, CancellationToken ct = default);

    // ── GDPR ──────────────────────────────────────────────────────────────

    /// <summary>Every row carrying this person's attribution, for the export.</summary>
    Task<WorkgroupUserRows> GetRowsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: nulls the person's attribution everywhere and deletes their
    /// membership rows. Content — comments, log bodies, minutes, documents — stays: it is
    /// the association's record (design §18). Idempotent.
    /// </summary>
    Task EraseUserAsync(Guid userId, Instant now, CancellationToken ct = default);

    // ── Account merge ─────────────────────────────────────────────────────

    /// <summary>
    /// Account-merge fold: re-points every user-id column from source to target and
    /// collapses the duplicate membership rows the fold creates, keeping the earliest
    /// join and the stronger role. Idempotent.
    /// </summary>
    Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default);
}
