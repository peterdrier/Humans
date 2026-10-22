using NodaTime;

namespace Humans.Grants.Domain;

internal sealed class GrantProgram
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    /// <summary>Shown verbatim in the notice and on the profile.</summary>
    public string Entitles { get; set; } = "";
    /// <summary>Never shown to members.</summary>
    public string? Description { get; set; }
    public int Year { get; set; }
    public GrantIssuer IssuedBy { get; set; }
    /// <summary>Set only when <see cref="IssuedBy"/> is Source.</summary>
    public string? SourceKey { get; set; }
    /// <summary>Default for every grant without its own expiry.</summary>
    public LocalDate? GrantsExpireOn { get; set; }
    public GrantProgramStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<GrantProgramManager> Managers { get; set; } = new List<GrantProgramManager>();
    public ICollection<Grant> Grants { get; set; } = new List<Grant>();
}

internal sealed class GrantProgramManager
{
    public Guid ProgramId { get; set; }
    public Guid UserId { get; set; }
    public Guid AddedByUserId { get; set; }
    public Instant AddedAt { get; set; }

    public GrantProgram Program { get; set; } = null!;
}
