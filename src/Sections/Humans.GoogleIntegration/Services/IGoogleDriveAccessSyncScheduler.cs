namespace Humans.GoogleIntegration.Services;

/// <summary>Keeps the service layer off the background-job runtime.</summary>
internal interface IGoogleDriveAccessSyncScheduler
{
    void Enqueue(string folderId);
}
