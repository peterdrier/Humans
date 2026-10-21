using NodaTime;

namespace Humans.Grants.Domain;

/// <summary>
/// One person entitled under one program. Table <c>grants</c>. A revoked grant keeps its row
/// as the record; at most one unrevoked grant per person per program.
/// </summary>
internal sealed class Grant
{
    public Guid Id { get; init; }
    public Guid ProgramId { get; set; }
    /// <summary>Bare cross-section reference to Users; no FK.</summary>
    public Guid UserId { get; set; }
    /// <summary>The issuing source's own id for this grant, when a section issued it.</summary>
    public string? SourceRef { get; set; }
    /// <summary>Overrides the program's <c>GrantsExpireOn</c> (Europe/Madrid date).</summary>
    public LocalDate? ExpiresOn { get; set; }
    public Instant IssuedAt { get; init; }
    public Guid IssuedByUserId { get; set; }
    /// <summary>Last time the notice was queued: at issue and on every resend.</summary>
    public Instant? NotifiedAt { get; set; }
    public Instant? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokeReason { get; set; }

    public GrantProgram Program { get; set; } = null!;
}
