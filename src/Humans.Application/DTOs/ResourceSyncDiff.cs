namespace Humans.Application.DTOs;

/// <summary>
/// Describes the drift between expected (DB) and actual (Google) state for a single resource.
/// </summary>
public class ResourceSyncDiff
{
    public Guid ResourceId { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string TeamName { get; init; } = string.Empty;
    public string? GoogleId { get; init; }
    public string? Url { get; init; }
    public string? ErrorMessage { get; init; }
    public List<string> MembersToAdd { get; init; } = [];
    public List<string> MembersToRemove { get; init; } = [];
    public bool IsInSync => MembersToAdd.Count == 0 && MembersToRemove.Count == 0 && ErrorMessage == null;
}

/// <summary>
/// Aggregated result of previewing all Google resource syncs.
/// </summary>
public class SyncPreviewResult
{
    public List<ResourceSyncDiff> Diffs { get; init; } = [];
    public int TotalResources => Diffs.Count;
    public int InSyncCount => Diffs.Count(d => d.IsInSync);
    public int DriftCount => Diffs.Count(d => !d.IsInSync);
}
