using Humans.Base.Enums;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

public interface IGoogleSyncLogViewer : IApplicationService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>Newest first, following merge tombstones.</summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetForUserAsync(Guid userId, CancellationToken ct = default);

}

/// <summary>The resource name is resolved.</summary>
public sealed record GoogleSyncLogView(
    GoogleSyncLogAction Action,
    Instant OccurredAt,
    string Description,
    string? ResourceName,
    string UserEmail,
    string Role,
    GoogleSyncSource Source,
    bool Success,
    string? ErrorMessage,
    Guid? UserId = null);

public enum GoogleSyncLogAction
{
    AccessGranted,
    AccessRevoked
}
