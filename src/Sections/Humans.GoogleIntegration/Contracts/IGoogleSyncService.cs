using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

public interface IGoogleSyncService : IApplicationService
{
    /// <summary>Unprocessed events with a <c>LastError</c>.</summary>
    Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets a section refuse a folder <see cref="IGoogleSyncWrite.CreateSubfolderAsync"/> would later 404 on.</summary>
    Task<bool> IsFolderReachableAsync(string folderId, CancellationToken cancellationToken = default);
}

public sealed record GoogleSyncOutboxEventSnapshot(
    Guid Id,
    string EventType,
    Guid TeamId,
    Guid UserId,
    Instant OccurredAt,
    Instant? ProcessedAt,
    int RetryCount,
    string? LastError,
    bool FailedPermanently);
