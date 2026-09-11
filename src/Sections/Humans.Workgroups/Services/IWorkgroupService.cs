using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// The Workgroups section's business surface: the register, the member work that happens
/// on a group's page, the Secretary's and the Board's decisions, and the daily rhythm
/// pass. The only caller of <c>IWorkgroupRepository</c>.
/// </summary>
/// <remarks>
/// Reads return the whole register or one group; the dataset is a few dozen rows, so
/// callers filter and sort in memory rather than asking for shape variants. Writes that
/// change who may see a group's Drive folder ask GoogleIntegration for a sync; writes
/// that change the register's state write a system log entry, an audit entry and the
/// notifications of §14.
/// </remarks>
internal interface IWorkgroupService : IApplicationService
{
    // ── Reads ─────────────────────────────────────────────────────────────

    /// <summary>Every register entry, whatever its status, with its children.</summary>
    Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default);

    Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default);

    Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Groups this person is a current member of, whatever their role.</summary>
    Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Membership check for the authorization handler; no page composition here.</summary>
    Task<bool> IsMemberAsync(Guid workgroupId, Guid userId, CancellationToken ct = default);

    // ── Applying and joining ──────────────────────────────────────────────

    /// <summary>
    /// Clause 1: applies for a group. The applicant becomes its first coordinator, the
    /// group starts <see cref="WorkgroupStatus.Applied"/>, and the Board is notified.
    /// </summary>
    Task<Guid> ApplyAsync(Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default);

    /// <summary>Standing approval (clause 3): joining is immediate. Requests a Drive sync.</summary>
    Task JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Leaves the group. The last coordinator must name a replacement first; a
    /// <c>BoardOrAdmin</c> caller may override that with <paramref name="asAdmin"/>.
    /// </summary>
    Task LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        bool asAdmin = false,
        CancellationToken ct = default);

    /// <summary>Any signed-in human, once per group per seven days. Notifies the coordinators.</summary>
    Task RequestStatusAsync(Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default);

    // ── Member work on the group page ─────────────────────────────────────

    /// <summary>Register fields. A changed deliverable sentence writes a ScopeChanged entry.</summary>
    Task EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default);

    /// <summary>
    /// Sets the one or two coordinators. Members hand over among themselves;
    /// <paramref name="asAdmin"/> lets the Board override (design §5).
    /// </summary>
    Task SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        bool asAdmin = false,
        CancellationToken ct = default);

    Task<Guid> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default);

    Task UpdateMeetingAsync(Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default);

    Task DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Adds a member log entry (Update, Disclosure or Note). System kinds are refused.</summary>
    Task<Guid> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default);

    Task UpdateLogEntryAsync(Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default);

    /// <summary>Deletes a member log entry. Audited, because the log itself keeps no tombstone.</summary>
    Task DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Attaches a survey the member authored in Surveys, by id. Writes SurveySubmitted.</summary>
    Task LinkSurveyAsync(Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default);

    /// <summary>A member ends the group: Dormant with reason Delivered or Abandoned.</summary>
    Task MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default);

    // ── Documents ─────────────────────────────────────────────────────────

    Task<Guid> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default);

    /// <summary>Last write wins. Refused once the document is Delivered — the body is frozen.</summary>
    Task UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default);

    /// <summary>Publishes a Draft. Requires a non-empty body; notifies the members.</summary>
    Task PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Opens the comment period. Published documents with at least one category only.</summary>
    Task OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default);

    /// <summary>Closes the window early or on time. Comments stay visible, read-only.</summary>
    Task CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Delivers to the audience: freezes the body and puts the Board on the clock.</summary>
    Task DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default);

    // ── Comments ──────────────────────────────────────────────────────────

    /// <summary>Any signed-in human, while the window is open, in one of the document's categories.</summary>
    Task<Guid> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default);

    /// <summary>The group's answer to one comment. Allowed after the window closes.</summary>
    Task RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default);

    /// <summary>One disposition and response applied to every still-Pending comment in a category.</summary>
    Task RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default);

    /// <summary>Moderation with a reason. Audited; the text stays readable to admins.</summary>
    Task HideCommentAsync(Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default);

    // ── The Secretary and the Board ───────────────────────────────────────

    /// <summary>
    /// Registers the group: creates its Drive subfolder, stores the id, then flips the
    /// status. A folder-creation failure leaves the group <see cref="WorkgroupStatus.Applied"/>
    /// and surfaces the error, so the Secretary can retry (design §6).
    /// </summary>
    Task RegisterAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default);

    Task ReferAsync(Guid workgroupId, Guid actorUserId, string? note, CancellationToken ct = default);

    Task RefuseAsync(Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default);

    Task WithdrawAsync(Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default);

    /// <summary>Closes a quiet group: Dormant with reason Quiet and the written reasons.</summary>
    Task CloseAsync(Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default);

    /// <summary>Reverses Dormant: the page unfreezes and the Drive folder goes writable again.</summary>
    Task ReactivateAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Bootstrapping (design §21): applies on behalf, backdated, immediately Active.</summary>
    Task<Guid> RegisterExistingAsync(Guid actorUserId, WorkgroupBootstrap bootstrap, CancellationToken ct = default);

    /// <summary>The Board's written reply to a delivered document.</summary>
    Task RecordDispositionAsync(
        Guid documentId,
        Guid actorUserId,
        WorkgroupDisposition disposition,
        string note,
        CancellationToken ct = default);

    // ── Settings ──────────────────────────────────────────────────────────

    /// <summary>The Drive folder every group's subfolder is created under, or null when unset.</summary>
    Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default);

    Task SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default);

    // ── The daily job ─────────────────────────────────────────────────────

    /// <summary>
    /// One pass of the reporting rhythm (design §13): notifies, flags and records.
    /// It never registers or closes a group — a human does that.
    /// </summary>
    Task RunDailyRhythmAsync(CancellationToken ct = default);

    // ── GDPR and account merge ────────────────────────────────────────────
    // Carried by CachingWorkgroupService (erasure and the merge fold change cached rows);
    // on the interface so the decorator reaches the inner service.

    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    Task EraseForUserAsync(Guid userId, CancellationToken ct);

    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}
