using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Domain;

/// <summary>One Workspace mutation attempt. Append-only.</summary>
internal sealed class GoogleSyncLogEntry
{
    public Guid Id { get; init; }

    public GoogleSyncLogAction Action { get; init; }

    /// <summary>The linked <c>google_resources</c> row, or <see cref="Guid.Empty"/> for an unlinked source claim.</summary>
    public Guid ResourceId { get; init; }

    /// <summary>The human the access belongs to, when the caller knows it.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Address at the time of the sync — denormalized so history survives anonymization.</summary>
    public string UserEmail { get; init; } = string.Empty;

    /// <summary>The Google role granted or revoked (e.g. "writer", "MEMBER").</summary>
    public string Role { get; init; } = string.Empty;

    public GoogleSyncSource Source { get; init; }

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string Description { get; init; } = string.Empty;

    public string JobName { get; init; } = string.Empty;

    public Instant OccurredAt { get; init; }
}
