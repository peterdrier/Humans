using NodaTime;

namespace Humans.Grants.Domain;

/// <summary>
/// One kind of entitlement for one event year: what a grant entitles the person to, who
/// fills it, and when its grants expire. Table <c>grant_programs</c>.
/// </summary>
internal sealed class GrantProgram
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    /// <summary>What a grant gives the person, in plain words; shown in the notice and on the profile.</summary>
    public string Entitles { get; set; } = "";
    /// <summary>Admin notes; never shown to members.</summary>
    public string? Description { get; set; }
    public int Year { get; set; }
    public GrantIssuer IssuedBy { get; set; }
    /// <summary>The section key allowed to issue when <see cref="IssuedBy"/> is Source; null otherwise.</summary>
    public string? SourceKey { get; set; }
    /// <summary>Default expiry (Europe/Madrid date) for every grant without its own.</summary>
    public LocalDate? GrantsExpireOn { get; set; }
    public GrantProgramStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<GrantProgramManager> Managers { get; set; } = new List<GrantProgramManager>();
    public ICollection<Grant> Grants { get; set; } = new List<Grant>();
}

/// <summary>One person who issues and revokes grants in one program. Table <c>grant_program_managers</c>.</summary>
internal sealed class GrantProgramManager
{
    public Guid ProgramId { get; set; }
    /// <summary>Bare cross-section reference to Users; no FK.</summary>
    public Guid UserId { get; set; }
    public Guid AddedByUserId { get; set; }
    public Instant AddedAt { get; set; }

    public GrantProgram Program { get; set; } = null!;
}
