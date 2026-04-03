using Humans.Application.DTOs;

namespace Humans.Web.Models;

public class TeamSyncViewModel
{
    public bool CanExecuteActions { get; set; }
}

public class SyncTabContentViewModel
{
    public required SyncPreviewResult Result { get; init; }
    public required string ResourceType { get; init; }
    public bool CanExecuteActions { get; init; }
    public bool CanViewAudit { get; init; }
}
