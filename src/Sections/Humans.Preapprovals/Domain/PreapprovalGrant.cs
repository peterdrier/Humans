using Humans.Preapprovals.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Domain;

/// <summary>
/// An application is a grant in Applied. The amount is never rewritten by the system: a revoked or unclaimed grant
/// keeps it so the gap is readable later.
/// </summary>
internal sealed class PreapprovalGrant
{
    public Guid Id { get; init; }
    public Guid ProgramId { get; set; }
    /// <summary>No FK.</summary>
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
    /// <summary>Set at Claimed, kept at Consumed and after a withdrawal.</summary>
    public Guid? ExpenseReportId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public PreapprovalProgram Program { get; set; } = null!;
}

/// <summary>
/// The reminder job's delivery log, keyed by the deadline announced so a moved deadline reminds again. Nothing reads it
/// but the job.
/// </summary>
internal sealed class ReminderSent
{
    public Guid GrantId { get; set; }
    public ReminderKind Kind { get; set; }
    public LocalDate ClaimBy { get; set; }
    public Instant SentAt { get; set; }
}
