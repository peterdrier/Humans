using Humans.GoogleIntegration.Contracts;
using Humans.Domain.Enums;

namespace Humans.GoogleIntegration.Models;

internal sealed class SyncSettingsViewModel
{
    public List<SyncServiceSettingViewModel> Settings { get; set; } = [];
}

internal sealed class SyncServiceSettingViewModel
{
    public SyncServiceType ServiceType { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public SyncMode CurrentMode { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByName { get; set; }
}
