using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>
/// Sync status of a single member relative to a resource.
/// </summary>
public record MemberSyncStatus(
    string Email,
    string DisplayName,
    MemberSyncState State,
    List<string> TeamNames,
    string? CurrentRole = null);

/// <summary>
/// Whether a member is correctly synced, missing, or extra.
/// </summary>
public enum MemberSyncState
{
    Correct,
    Missing,
    Extra,
    Inherited
}

/// <summary>
/// Describes the drift between expected (DB) and actual (Google) state for a single resource.
/// </summary>
public class ResourceSyncDiff
{
    public Guid ResourceId { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string? GoogleId { get; init; }
    public string? Url { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The permission level team members will get on this resource.</summary>
    public string? PermissionLevel { get; init; }

    /// <summary>All teams that link to this resource.</summary>
    public List<string> LinkedTeams { get; init; } = [];

    /// <summary>Per-member sync status (correct, missing, extra).</summary>
    public List<MemberSyncStatus> Members { get; init; } = [];

    // Convenience properties
    public List<string> MembersToAdd => Members
        .Where(m => m.State == MemberSyncState.Missing)
        .Select(m => m.Email).ToList();
    public List<string> MembersToRemove => Members
        .Where(m => m.State == MemberSyncState.Extra)
        .Select(m => m.Email).ToList();
    public bool IsInSync => !Members.Any(m => m.State is MemberSyncState.Missing or MemberSyncState.Extra) && ErrorMessage == null;
}

/// <summary>
/// Aggregated result of syncing/previewing resources of a given type.
/// </summary>
public class SyncPreviewResult
{
    public List<ResourceSyncDiff> Diffs { get; init; } = [];
    public int TotalResources => Diffs.Count;
    public int InSyncCount => Diffs.Count(d => d.IsInSync);
    public int DriftCount => Diffs.Count(d => !d.IsInSync && d.ErrorMessage == null);
    public int ErrorCount => Diffs.Count(d => d.ErrorMessage != null);
}
