using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using Humans.Base.Models;

namespace Humans.Governance.Models;

internal sealed class AdminApplicationListViewModel : PagedListViewModel
{
    public List<AdminApplicationViewModel> Applications { get; set; } = [];
    public string? StatusFilter { get; set; }
    public string? TierFilter { get; set; }
}

internal sealed class AdminApplicationViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public ApplicationStatus Status { get; set; }
    public string StatusBadgeClass { get; set; } = "bg-secondary";
    public DateTime SubmittedAt { get; set; }
    public string MotivationPreview { get; set; } = string.Empty;
    public MembershipTier MembershipTier { get; set; }
}

internal sealed class AdminApplicationDetailViewModel : ApplicationDetailViewModelBase
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string? Language { get; set; }
    public bool CanApproveReject { get; set; }
}

internal sealed class AdminApplicationActionModel
{
    public Guid ApplicationId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
