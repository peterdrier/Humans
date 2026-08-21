using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Domain;

/// <summary>
/// One Google Workspace membership/permission mutation attempt. Append-only:
/// the section writes rows and never updates or deletes them.
/// </summary>
internal sealed class GoogleSyncLogEntry
{
    public Guid Id { get; init; }

    /// <summary>What the sync did to the resource.</summary>
    public GoogleSyncLogAction Action { get; init; }

    /// <summary>The <c>google_resources</c> row the mutation targeted.</summary>
    public Guid ResourceId { get; init; }

    /// <summary>The human the access belongs to, when the caller knows it.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Address at the time of the sync — denormalized so history survives anonymization.</summary>
    public string UserEmail { get; init; } = string.Empty;

    /// <summary>The Google role granted or revoked (e.g. "writer", "MEMBER").</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>What triggered the sync.</summary>
    public GoogleSyncSource Source { get; init; }

    /// <summary>Whether the Google API call succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error detail when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>The service or job that performed the sync.</summary>
    public string JobName { get; init; } = string.Empty;

    public Instant OccurredAt { get; init; }
}
