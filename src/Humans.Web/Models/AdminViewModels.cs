namespace Humans.Web.Models;

public class AdminDashboardViewModel
{
    public int TotalMembers { get; set; }
    public int ActiveMembers { get; set; }
    public int PendingVolunteers { get; set; }
    public int PendingApplications { get; set; }
    public int PendingConsents { get; set; }
    public List<RecentActivityViewModel> RecentActivity { get; set; } = [];
}

public class RecentActivityViewModel
{
    public string Description { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = string.Empty;
}

public class AdminMemberListViewModel
{
    public List<AdminMemberViewModel> Members { get; set; } = [];
    public string? SearchTerm { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class AdminMemberViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string MembershipStatus { get; set; } = "None";
    public bool HasProfile { get; set; }
    public bool IsApproved { get; set; }
}

public class AdminMemberDetailViewModel
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // Profile
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public bool IsSuspended { get; set; }
    public bool IsApproved { get; set; }
    public bool HasProfile { get; set; }
    public string? AdminNotes { get; set; }

    // Stats
    public int ApplicationCount { get; set; }
    public int ConsentCount { get; set; }
    public List<AdminMemberApplicationViewModel> Applications { get; set; } = [];
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public List<AuditLogEntryViewModel> AuditLog { get; set; } = [];
}

public class AdminMemberApplicationViewModel
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; }
}

public class AdminApplicationListViewModel
{
    public List<AdminApplicationViewModel> Applications { get; set; } = [];
    public string? StatusFilter { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class AdminApplicationViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusBadgeClass { get; set; } = "bg-secondary";
    public DateTime SubmittedAt { get; set; }
    public string MotivationPreview { get; set; } = string.Empty;
}

public class AdminApplicationDetailViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
    public string? UserProfilePictureUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Motivation { get; set; } = string.Empty;
    public string? AdditionalInfo { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ReviewStartedAt { get; set; }
    public string? ReviewerName { get; set; }
    public string? ReviewNotes { get; set; }
    public bool CanStartReview { get; set; }
    public bool CanApproveReject { get; set; }
    public List<ApplicationHistoryViewModel> History { get; set; } = [];
}

public class AdminApplicationActionModel
{
    public Guid ApplicationId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class AdminRoleAssignmentListViewModel
{
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public string? RoleFilter { get; set; }
    public bool ShowInactive { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class AdminRoleAssignmentViewModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateRoleAssignmentViewModel
{
    public Guid UserId { get; set; }
    public string UserDisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<string> AvailableRoles { get; set; } = [];
}

public class EndRoleAssignmentViewModel
{
    public Guid Id { get; set; }
    public string UserDisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class AuditLogEntryViewModel
{
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public bool IsSystemAction { get; set; }
}

public class AuditLogListViewModel
{
    public List<AuditLogEntryViewModel> Entries { get; set; } = [];
    public string? ActionFilter { get; set; }
    public int AnomalyCount { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class GoogleSyncAuditEntryViewModel
{
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? UserEmail { get; set; }
    public string? Role { get; set; }
    public string? SyncSource { get; set; }
    public DateTime OccurredAt { get; set; }
    public bool? Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string? ResourceName { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? RelatedEntityId { get; set; }
}

public class GoogleSyncAuditListViewModel
{
    public List<GoogleSyncAuditEntryViewModel> Entries { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string? BackUrl { get; set; }
    public string? BackLabel { get; set; }
}
