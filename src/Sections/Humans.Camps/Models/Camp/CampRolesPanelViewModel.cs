namespace Humans.Camps.Models;

internal sealed class CampRolesPanelViewModel
{
    public required Guid CampSeasonId { get; init; }
    public required string CampSlug { get; init; }
    public required IReadOnlyList<CampRoleRowViewModel> Rows { get; init; }
    public required IReadOnlyList<CampMemberPickerOption> ActiveMembers { get; init; }
    public required bool CanManage { get; init; }
}

internal sealed record CampMemberPickerOption(Guid CampMemberId, Guid UserId, string DisplayName);
