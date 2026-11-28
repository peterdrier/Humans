using Humans.Google.Domain;

namespace Humans.Google.Models;

internal sealed class SyncServiceSettingViewModel
{
    public SyncServiceType ServiceType { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public SyncMode CurrentMode { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
