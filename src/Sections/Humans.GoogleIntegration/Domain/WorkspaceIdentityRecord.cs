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

    /// <summary>
    /// The address Drive accepted in place of a non-Gmail <c>local+tag@domain</c> <see cref="Email"/>
    /// that Drive refused (peterdrier/Humans#2037). Drive-only: Groups and other Google services treat
    /// plus addresses differently, so they keep using <see cref="Email"/>. Cleared when
    /// <see cref="Email"/> changes.
    /// </summary>
    public string? DriveEmail { get; set; }

    /// <summary>Google's verdict on <see cref="Email"/>.</summary>
    public GoogleEmailStatus Status { get; set; }

    public Instant UpdatedAt { get; set; }
}
