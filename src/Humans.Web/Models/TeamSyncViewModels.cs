using Humans.Application.DTOs;

namespace Humans.Web.Models;

public class TeamSyncViewModel;

public class SyncTabContentViewModel
{
    public required SyncPreviewResult Result { get; init; }
    public required string ResourceType { get; init; }
}
