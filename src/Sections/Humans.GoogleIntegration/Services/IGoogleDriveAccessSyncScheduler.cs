using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Queues Drive access fan-out reconciliation work without exposing the
/// Application layer to the background-job runtime. Mirrors
/// <see cref="IGoogleGroupSyncScheduler"/> for <see cref="IGoogleDriveSync"/>.
/// </summary>
internal interface IGoogleDriveAccessSyncScheduler
{
    void Enqueue(string folderId);
}
