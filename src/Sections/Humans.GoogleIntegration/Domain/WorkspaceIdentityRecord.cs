using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Domain;

/// <summary>
/// One row per member. Supersedes <c>user_emails.IsGoogle</c> and <c>GoogleEmailStatus</c>, which Users leaves
/// unwritten until the <c>google.workspace-identities</c> data migration has run in production.
/// </summary>
internal sealed class WorkspaceIdentityRecord
{
    /// <summary>Bare id, no FK to <c>users</c>.</summary>
    public Guid UserId { get; init; }

    /// <summary>The designated address. Null when the designation was cleared: the row then
    /// records that, so the read never falls back to the legacy <c>IsGoogle</c> flag.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// The address Drive accepted after refusing a non-Gmail <c>local+tag@domain</c> <see cref="Email"/>. Drive only:
    /// other Google services keep using <see cref="Email"/>. Cleared when <see cref="Email"/> changes.
    /// </summary>
    public string? DriveEmail { get; set; }

    /// <summary>Google's verdict on <see cref="Email"/>.</summary>
    public GoogleEmailStatus Status { get; set; }

    public Instant UpdatedAt { get; set; }
}
