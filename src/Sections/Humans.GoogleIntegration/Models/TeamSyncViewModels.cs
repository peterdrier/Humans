using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Models;

internal sealed class TeamSyncViewModel;

internal sealed class SyncTabContentViewModel
{
    public required SyncPreviewResult Result { get; init; }
    public required string ResourceType { get; init; }
}
