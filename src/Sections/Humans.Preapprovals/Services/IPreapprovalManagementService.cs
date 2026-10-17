using Humans.Preapprovals.Contracts;

namespace Humans.Preapprovals.Services;

/// <summary>
/// The section's full surface: program and manager administration, issuing, applying, approving,
/// declining and revoking, plus the reminder pass. Internal, the
/// <c>ITeamManagementService : ITeamService</c> pattern — only the section's own controllers,
/// job and tests see it; Expenses sees <see cref="IPreapprovalService"/>. Authorization is the
/// controllers' job: every method trusts its actor.
/// </summary>
internal interface IPreapprovalManagementService : IPreapprovalService
{
    // ── Reads ─────────────────────────────────────────────────────────────

    Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default);
    Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default);

    /// <summary>Every grant of the person, any status, across every id they have held.</summary>
    Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Nav gate: the person has any grant, manages a program, or a program accepts applications.</summary>
    Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Nav and admin-index gate for people without an admin role.</summary>
    Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default);

    // ── Programs: Board, FinanceAdmin, Admin ──────────────────────────────

    Task<PreapprovalMutationResult<Guid>> CreateProgramAsync(ProgramSave save, Guid actorUserId, CancellationToken ct = default);
    Task<PreapprovalMutationResult> UpdateProgramAsync(Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default);

    // ── Grants: a manager of the program or above ─────────────────────────

    /// <summary><paramref name="amount"/> is required on a per-grant program and refused on a fixed one.</summary>
    Task<PreapprovalMutationResult<Guid>> IssueAsync(
        Guid programId, Guid beneficiaryUserId, decimal? amount, string? label, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>Fixed-per-person programs only: one grant at the per-person amount each, all or nothing.</summary>
    Task<PreapprovalMutationResult<IReadOnlyList<Guid>>> IssueManyAsync(
        Guid programId, IReadOnlyList<Guid> beneficiaryUserIds, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Approve an application. <paramref name="amount"/> may lower a per-grant request; ignored on fixed programs.</summary>
    Task<PreapprovalMutationResult> ApproveAsync(Guid grantId, decimal? amount, Guid actorUserId, CancellationToken ct = default);
    Task<PreapprovalMutationResult> DeclineAsync(Guid grantId, string? note, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Open or Claimed → Revoked. The amount and any report id are kept.</summary>
    Task<PreapprovalMutationResult> RevokeAsync(Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Open or Claimed only; ceiling-checked and audited with old and new.</summary>
    Task<PreapprovalMutationResult> ChangeAmountAsync(Guid grantId, decimal amount, Guid actorUserId, CancellationToken ct = default);

    // ── Members ───────────────────────────────────────────────────────────

    /// <summary>One application per person per program; a second submit edits the pending one.</summary>
    Task<PreapprovalMutationResult<Guid>> ApplyAsync(
        Guid programId, Guid applicantUserId, string? note, decimal? requestedAmount, CancellationToken ct = default);

    // ── Job ───────────────────────────────────────────────────────────────

    /// <summary>The nightly pass: 14-day and 3-day reminders to Open grant holders, each recorded once per deadline.</summary>
    Task SendDeadlineRemindersAsync(CancellationToken ct = default);
}
