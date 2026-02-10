namespace Humans.Web.Models;

public class GoogleSyncViewModel
{
    public int TotalResources { get; set; }
    public int InSyncCount { get; set; }
    public int DriftCount { get; set; }
    public int ErrorCount { get; set; }
    public List<GoogleSyncResourceViewModel> Resources { get; set; } = [];
}

public class GoogleSyncResourceViewModel
{
    public Guid ResourceId { get; set; }
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsInSync { get; set; }
    public List<string> MembersToAdd { get; set; } = [];
    public List<string> MembersToRemove { get; set; } = [];
}
