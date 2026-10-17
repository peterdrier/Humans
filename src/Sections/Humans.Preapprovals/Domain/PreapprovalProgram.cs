using NodaTime;

namespace Humans.Preapprovals.Domain;

/// <summary>
/// One event-scoped pool of pre-approvals: its Holded account, its rules, optionally its total
/// ceiling, its managers and its claim deadline. Table <c>pre_programs</c>.
/// </summary>
internal sealed class PreapprovalProgram
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ProgramKind Kind { get; set; }
    /// <summary>Required when <see cref="ProgramKind.FixedPerPerson"/>; refused otherwise.</summary>
    public decimal? PerPersonAmount { get; set; }
    /// <summary>Null means no ceiling. Edits are audited with old and new value.</summary>
    public decimal? TotalCeiling { get; set; }
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public bool AcceptsApplications { get; set; }
    /// <summary>Reports must be submitted on or before this date (Europe/Madrid, end of day).</summary>
    public LocalDate ClaimBy { get; set; }
    public ProgramStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<ProgramManager> Managers { get; set; } = new List<ProgramManager>();
    public ICollection<PreapprovalGrant> Grants { get; set; } = new List<PreapprovalGrant>();
}

/// <summary>One person allowed to issue, approve and revoke grants in one program. Table <c>pre_program_managers</c>.</summary>
internal sealed class ProgramManager
{
    public Guid ProgramId { get; set; }
    /// <summary>Bare cross-section reference to Users; no FK.</summary>
    public Guid UserId { get; set; }
    public Guid AddedByUserId { get; set; }
    public Instant AddedAt { get; set; }

    public PreapprovalProgram Program { get; set; } = null!;
}
