using NodaTime;

namespace Humans.Preapprovals.Domain;

internal sealed class PreapprovalProgram
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ProgramKind Kind { get; set; }
    /// <summary>Required when <see cref="ProgramKind.FixedPerPerson"/>; refused otherwise.</summary>
    public decimal? PerPersonAmount { get; set; }
    /// <summary>Null means no ceiling.</summary>
    public decimal? TotalCeiling { get; set; }
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public bool AcceptsApplications { get; set; }
    /// <summary>Europe/Madrid, end of day.</summary>
    public LocalDate ClaimBy { get; set; }
    public ProgramStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<ProgramManager> Managers { get; set; } = new List<ProgramManager>();
    public ICollection<PreapprovalGrant> Grants { get; set; } = new List<PreapprovalGrant>();
}

internal sealed class ProgramManager
{
    public Guid ProgramId { get; set; }
    /// <summary>No FK.</summary>
    public Guid UserId { get; set; }
    public Guid AddedByUserId { get; set; }
    public Instant AddedAt { get; set; }

    public PreapprovalProgram Program { get; set; } = null!;
}
