
namespace Humans.GoogleIntegration.Contracts;

public record TeamLink(string Name, string Slug, string? PermissionLevel = null);

public record MemberSyncStatus(
    string Email,
    string DisplayName,
    MemberSyncState State,
    List<TeamLink> TeamLinks,
    string? CurrentRole = null,
    string? ExpectedRole = null,
    Guid? UserId = null,
    string? ProfilePictureUrl = null);

public enum MemberSyncState
{
    Correct,
    Missing,
    Extra,
    Inherited,
    /// <summary>Has access at a lower level than expected (multi-team max resolution).</summary>
    WrongRole,
    /// <summary>
    /// Drive only: member holds a direct role above what the sources claim (e.g. raised by
    /// hand). AddAndRemove lowers it to the expected role; Preview lists it beforehand.
    /// </summary>
    Downgrade
}

public class ResourceSyncDiff
{
    public Guid ResourceId { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string? GoogleId { get; init; }
    public string? Url { get; init; }
    public string? ErrorMessage { get; init; }

    public string? PermissionLevel { get; init; }

    public List<TeamLink> LinkedTeams { get; init; } = [];

    public List<MemberSyncStatus> Members { get; init; } = [];

    public List<string> MembersToAdd => Members
        .Where(m => m.State == MemberSyncState.Missing)
        .Select(m => m.Email).ToList();
    public List<string> MembersToRemove => Members
        .Where(m => m.State == MemberSyncState.Extra)
        .Select(m => m.Email).ToList();
    public bool IsInSync => !Members.Any(m => m.State is MemberSyncState.Missing or MemberSyncState.Extra or MemberSyncState.WrongRole or MemberSyncState.Downgrade) && ErrorMessage is null;
}

public class SyncPreviewResult
{
    public List<ResourceSyncDiff> Diffs { get; init; } = [];
    public int TotalResources => Diffs.Count;
    public int InSyncCount => Diffs.Count(d => d.IsInSync);
    public int DriftCount => Diffs.Count(d => !d.IsInSync && d.ErrorMessage is null);
    public int ErrorCount => Diffs.Count(d => d.ErrorMessage is not null);
}
