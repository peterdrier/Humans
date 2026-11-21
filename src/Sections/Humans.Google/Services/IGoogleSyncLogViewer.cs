using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Google.Domain;
using NodaTime;

namespace Humans.Google.Services;

internal interface IGoogleSyncLogViewer : IApplicationService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>Newest first, following merge tombstones.</summary>
    Task<IReadOnlyList<GoogleSyncLogView>> GetForUserAsync(Guid userId, CancellationToken ct = default);

}

/// <summary>The resource name is resolved.</summary>
internal sealed record GoogleSyncLogView(
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
