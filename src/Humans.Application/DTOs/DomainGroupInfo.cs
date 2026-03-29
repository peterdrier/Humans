namespace Humans.Application.DTOs;

public class DomainGroupInfo
{
    public string GroupEmail { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? GoogleId { get; init; }
    public int MemberCount { get; init; }
    public string? LinkedTeamName { get; init; }
    public Guid? LinkedTeamId { get; init; }
    public Dictionary<string, string> ActualSettings { get; init; } = [];
    public List<GroupSettingDrift> Drifts { get; init; } = [];
    public bool HasDrift => Drifts.Count > 0;
    public string? ErrorMessage { get; init; }
}

public class AllGroupsResult
{
    public List<DomainGroupInfo> Groups { get; init; } = [];
    public Dictionary<string, string> ExpectedSettings { get; init; } = [];
    public string? ErrorMessage { get; init; }
}
