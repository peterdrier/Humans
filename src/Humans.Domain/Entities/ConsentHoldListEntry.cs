using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// An entry on the Consent Hold List. If a pending human's legal name matches
/// one of these entries (per the LLM auto-approval job), the human is NOT
/// auto-approved and must be reviewed by a Consent Coordinator manually.
/// Admin-only CRUD.
/// </summary>
public class ConsentHoldListEntry
{
    /// <summary>
    /// Auto-increment primary key.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Free-form name / alias text to match against incoming legal names.
    /// Case-insensitive match semantics are handled by the LLM, not by SQL.
    /// </summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>
    /// Optional admin note explaining why this entry is on the list.
    /// Never shown to humans (non-admin) — internal only.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// FK to the User (AspNetUsers) that added this entry.
    /// </summary>
    public Guid AddedByUserId { get; init; }

    /// <summary>
    /// When this entry was added.
    /// </summary>
    public Instant AddedAt { get; init; }
}
