using NodaTime;

namespace Humans.Grants.Domain;

/// <summary>A revoked grant keeps its row as the record.</summary>
internal sealed class Grant
{
    public Guid Id { get; init; }
    public Guid ProgramId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Set when a section issued it.</summary>
    public string? SourceRef { get; set; }
    /// <summary>Overrides the program's <c>GrantsExpireOn</c>.</summary>
    public LocalDate? ExpiresOn { get; set; }
    public Instant IssuedAt { get; init; }
    public Guid IssuedByUserId { get; set; }
    /// <summary>Set at issue and on every resend.</summary>
    public Instant? NotifiedAt { get; set; }
    public Instant? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokeReason { get; set; }

    public GrantProgram Program { get; set; } = null!;
}
