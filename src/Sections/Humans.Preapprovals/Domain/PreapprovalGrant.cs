using Humans.Preapprovals.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Domain;

/// <summary>
/// One person pre-approved under a program for one amount; an application is a grant in
/// <see cref="GrantStatus.Applied"/>. Table <c>pre_grants</c>. The amount is never rewritten by
/// the system: a revoked or unclaimed grant keeps it so the gap is readable later.
/// </summary>
internal sealed class PreapprovalGrant
{
    public Guid Id { get; init; }
    public Guid ProgramId { get; set; }
    /// <summary>Bare cross-section reference to Users; no FK.</summary>
    public Guid BeneficiaryUserId { get; set; }
    /// <summary>The art project name; null for travel.</summary>
    public string? Label { get; set; }
    public decimal MaxAmount { get; set; }
    public GrantStatus Status { get; set; }
    /// <summary>What the applicant wrote; null when issued directly.</summary>
    public string? ApplicationNote { get; set; }
    /// <summary>What the applicant asked for on a per-grant program; the manager may approve less.</summary>
    public decimal? RequestedAmount { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public Instant? DecidedAt { get; set; }
    /// <summary>Bare reference to Expenses. Set at Claimed, kept at Consumed and after a withdrawal.</summary>
    public Guid? ExpenseReportId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public PreapprovalProgram Program { get; set; } = null!;
}

/// <summary>
/// The reminder job's delivery log: one row per reminder actually sent, keyed by the deadline
/// it announced so a moved deadline runs the reminders again. Table <c>pre_reminders_sent</c>.
/// Not grant state; nothing reads it but the job.
/// </summary>
internal sealed class ReminderSent
{
    public Guid GrantId { get; set; }
    public ReminderKind Kind { get; set; }
    public LocalDate ClaimBy { get; set; }
    public Instant SentAt { get; set; }
}
