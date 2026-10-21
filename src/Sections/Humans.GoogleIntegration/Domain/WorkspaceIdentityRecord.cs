using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Domain;

/// <summary>
/// A member's designated Google Workspace identity, one row per member
/// (<c>google_workspace_identities</c>). Replaces <c>user_emails.IsGoogle</c> and
/// <c>user_emails.GoogleEmailStatus</c>, which Users keeps unwritten until the
/// <c>google.workspace-identities</c> data migration is done in production.
/// </summary>
internal sealed class WorkspaceIdentityRecord
{
    /// <summary>The member. Bare id, no FK to <c>users</c>
    /// (memory/architecture/no-cross-section-ef-joins.md).</summary>
    public Guid UserId { get; init; }

    /// <summary>The designated address. Null when the designation was cleared: the row then
    /// records that, so the read never falls back to the legacy <c>IsGoogle</c> flag.</summary>
    public string? Email { get; set; }

    /// <summary>Google's verdict on <see cref="Email"/>.</summary>
    public GoogleEmailStatus Status { get; set; }

    public Instant UpdatedAt { get; set; }
}
