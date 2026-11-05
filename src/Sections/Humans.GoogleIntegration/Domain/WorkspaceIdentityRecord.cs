using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Domain;

/// <summary>A member's Workspace identity designation and sync verdict.</summary>
internal sealed class WorkspaceIdentityRecord
{
    /// <summary>Bare id, no FK to <c>users</c>.</summary>
    public Guid UserId { get; init; }

    /// <summary>The designated address, or null when the designation was cleared.</summary>
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
