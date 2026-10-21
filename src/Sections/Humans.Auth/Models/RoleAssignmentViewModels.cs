using Humans.Base.Models;

namespace Humans.Auth.Models;

internal sealed class AdminRoleAssignmentListViewModel() : PagedListViewModel(50)
{
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public string? RoleFilter { get; set; }
    public bool ShowInactive { get; set; }
}

internal sealed class AdminRoleAssignmentViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class CreateRoleAssignmentViewModel
{
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<string> AvailableRoles { get; set; } = [];
}
