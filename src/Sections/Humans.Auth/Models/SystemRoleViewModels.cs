using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Auth.Models;

internal sealed class AdminSystemRoleListViewModel() : PagedListViewModel(50)
{
    public List<AdminSystemRoleViewModel> SystemRoles { get; set; } = [];
    public string? RoleFilter { get; set; }
    public bool ShowInactive { get; set; }
}

internal sealed class AdminSystemRoleViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
}

internal sealed class CreateSystemRoleViewModel
{
    public Guid UserId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public IReadOnlyList<RoleDefinition> AvailableRoles { get; set; } = [];
}
