using Humans.Base.Interfaces.Repositories;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Data;

/// <summary>The whole register in one read; the service works over it in memory.</summary>
internal sealed record WorkgroupsGraph(IReadOnlyList<Workgroup> Workgroups);

/// <summary>Every row carrying one person's attribution: the GDPR export and erasure surface.</summary>
internal sealed record WorkgroupUserRows(
    IReadOnlyList<Workgroup> AppliedFor,
    IReadOnlyList<WorkgroupMember> Memberships,
    IReadOnlyList<WorkgroupLogEntry> LogEntries,
    IReadOnlyList<WorkgroupMeeting> Meetings,
    IReadOnlyList<WorkgroupDocument> Documents,
    IReadOnlyList<WorkgroupDocumentComment> Comments);

/// <summary>Single-row getters return detached entities the service mutates and hands back to an <c>Update…</c> call.</summary>
internal interface IWorkgroupRepository : IRepository
{
    Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default);
    Task SetRootDriveFolderIdAsync(string folderId, CancellationToken ct = default);

    /// <summary>Detached, with every child collection loaded.</summary>
    Task<WorkgroupsGraph> GetGraphAsync(CancellationToken ct = default);

    /// <summary>One group with members, meetings, log entries and documents loaded.</summary>
    Task<Workgroup?> GetWorkgroupAsync(Guid id, CancellationToken ct = default);

    Task<bool> SlugTakenAsync(string slug, Guid? exceptId = null, CancellationToken ct = default);

    Task<WorkgroupMeeting?> GetMeetingAsync(Guid id, CancellationToken ct = default);

    Task<WorkgroupLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>Document with its comments loaded.</summary>
    Task<WorkgroupDocument?> GetDocumentAsync(Guid id, CancellationToken ct = default);

    /// <summary>Comment with its document loaded (the window and status rules need it).</summary>
    Task<WorkgroupDocumentComment?> GetCommentAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts the group with the rows created in the same act (members, the log entry).</summary>
    Task AddWorkgroupAsync(Workgroup workgroup, CancellationToken ct = default);

    /// <summary>Group fields, optional system log entries and explicit member changes in one save.</summary>
    Task UpdateWorkgroupAsync(
        Workgroup workgroup, CancellationToken ct = default, IReadOnlyList<WorkgroupLogEntry>? logEntries = null,
        IReadOnlyList<WorkgroupMember>? membersToUpdate = null);

    /// <summary>Membership and its optional system log entries in one save.</summary>
    Task AddMemberAsync(
        WorkgroupMember member, CancellationToken ct = default, IReadOnlyList<WorkgroupLogEntry>? logEntries = null);

    /// <summary>Membership changes and their optional system log entries in one save.</summary>
    Task UpdateMembersAsync(
        IReadOnlyList<WorkgroupMember> members, CancellationToken ct = default,
        IReadOnlyList<WorkgroupLogEntry>? logEntries = null);

    Task AddMeetingAsync(WorkgroupMeeting meeting, CancellationToken ct = default);

    Task UpdateMeetingAsync(WorkgroupMeeting meeting, CancellationToken ct = default);

    Task AddLogEntryAsync(WorkgroupLogEntry entry, CancellationToken ct = default);

    Task UpdateLogEntryAsync(WorkgroupLogEntry entry, CancellationToken ct = default);

    /// <summary>Hard delete. Never called for system entries.</summary>
    Task DeleteLogEntryAsync(Guid id, CancellationToken ct = default);

    Task AddDocumentAsync(WorkgroupDocument document, CancellationToken ct = default);

    Task UpdateDocumentAsync(WorkgroupDocument document, CancellationToken ct = default);

    Task AddCommentAsync(WorkgroupDocumentComment comment, CancellationToken ct = default);

    Task UpdateCommentsAsync(IReadOnlyList<WorkgroupDocumentComment> comments, CancellationToken ct = default);

    Task<WorkgroupUserRows> GetRowsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Nulls attribution and deletes membership rows. Content stays: it is the association's record. Idempotent.
    /// </summary>
    Task EraseUserAsync(Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Collapses the duplicate membership rows the fold creates, keeping the earliest join and the stronger role.
    /// Idempotent.
    /// </summary>
    Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default);
}
