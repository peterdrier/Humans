namespace Humans.Web.Models;

public class StaffViewModel
{
    public IReadOnlyList<StaffRoleSectionViewModel> RoleSections { get; init; } = [];
}

public class StaffRoleSectionViewModel
{
    public string RoleName { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public string Blurb { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public IReadOnlyList<StaffRoleHolderViewModel> Holders { get; init; } = [];
}

public class StaffRoleHolderViewModel
{
    public Guid UserId { get; init; }
}
