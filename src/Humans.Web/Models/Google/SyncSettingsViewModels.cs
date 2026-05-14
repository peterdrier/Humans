using Humans.Domain.Enums;

namespace Humans.Web.Models.Google;

public class SyncSettingsViewModel
{
    public List<SyncServiceSettingViewModel> Settings { get; set; } = [];
}

public class SyncServiceSettingViewModel
{
    public SyncServiceType ServiceType { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public SyncMode CurrentMode { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByName { get; set; }
}
