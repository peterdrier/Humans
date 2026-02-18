using Humans.Domain.Enums;

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

public class AdminHumanListViewModel
{
    public List<AdminHumanViewModel> Humans { get; set; } = [];
    public string? SearchTerm { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class AdminHumanViewModel
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

public class AdminHumanDetailViewModel
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
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public bool IsSuspended { get; set; }
    public bool IsApproved { get; set; }
    public bool HasProfile { get; set; }
    public string? AdminNotes { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ConsentCheckStatus? ConsentCheckStatus { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelationship { get; set; }

    // Rejection
    public bool IsRejected { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectedByName { get; set; }

    // Stats
    public int ApplicationCount { get; set; }
    public int ConsentCount { get; set; }
    public List<AdminHumanApplicationViewModel> Applications { get; set; } = [];
    public List<AdminRoleAssignmentViewModel> RoleAssignments { get; set; } = [];
    public List<AuditLogEntryViewModel> AuditLog { get; set; } = [];
}

public class AdminHumanApplicationViewModel
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; }
}

public class AdminApplicationListViewModel
{
    public List<AdminApplicationViewModel> Applications { get; set; } = [];
    public string? StatusFilter { get; set; }
    public string? TierFilter { get; set; }
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
    public string MembershipTier { get; set; } = string.Empty;
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
    public string? Language { get; set; }
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

public class ConfigurationItemViewModel
{
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public bool IsSet { get; set; }
    public string? Preview { get; set; }
    public bool IsRequired { get; set; }
}

public class AdminConfigurationViewModel
{
    public List<ConfigurationItemViewModel> Items { get; set; } = [];
}

public class EmailPreviewViewModel
{
    public Dictionary<string, List<EmailPreviewItem>> Previews { get; set; } = new(StringComparer.Ordinal);
}

public class EmailPreviewItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
