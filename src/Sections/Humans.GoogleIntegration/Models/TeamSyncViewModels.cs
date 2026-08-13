using Humans.GoogleIntegration.Contracts;
using Humans.Application.DTOs;

namespace Humans.GoogleIntegration.Models;

internal sealed class TeamSyncViewModel;

internal sealed class SyncTabContentViewModel
{
    public required SyncPreviewResult Result { get; init; }
    public required string ResourceType { get; init; }
}
