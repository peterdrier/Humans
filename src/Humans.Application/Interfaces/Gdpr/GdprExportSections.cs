namespace Humans.Application.Interfaces.Gdpr;

/// <summary>
/// Stable JSON top-level keys used in the GDPR data export document. Every
/// <see cref="IUserDataContributor"/> uses these constants when constructing
/// <see cref="UserDataSlice"/> values so the output shape stays stable even if
/// a contributor moves between services. The names are deliberately
/// <see cref="string"/>-typed so they can also appear in the architecture test
/// and in <c>docs/features/gdpr-export.md</c>.
///
/// <para>
/// Changing a value here is a breaking change for any human who has previously
/// downloaded their export and expects the same JSON keys on a re-download.
/// Add new sections, don't rename existing ones.
/// </para>
/// </summary>
public static class GdprExportSections
{
    public const string Account = "Account";
    public const string UserEmails = "UserEmails";
    public const string Profile = "Profile";
    public const string ContactFields = "ContactFields";
    public const string VolunteerHistory = "VolunteerHistory";
    public const string Languages = "Languages";
    public const string Applications = "Applications";
    public const string Consents = "Consents";
    public const string TeamMemberships = "TeamMemberships";
    public const string TeamJoinRequests = "TeamJoinRequests";
    public const string RoleAssignments = "RoleAssignments";
    public const string CommunicationPreferences = "CommunicationPreferences";
    public const string ShiftSignups = "ShiftSignups";
    public const string VolunteerEventProfiles = "VolunteerEventProfiles";
    public const string GeneralAvailability = "GeneralAvailability";
    public const string ShiftTagPreferences = "ShiftTagPreferences";
    public const string FeedbackReports = "FeedbackReports";
    public const string Notifications = "Notifications";
    public const string TicketOrders = "TicketOrders";
    public const string TicketAttendeeMatches = "TicketAttendeeMatches";
    public const string CampaignGrants = "CampaignGrants";
    public const string CampLeadAssignments = "CampLeadAssignments";
    public const string CampRoleAssignments = "CampRoleAssignments";
    public const string AccountMergeRequests = "AccountMergeRequests";
    public const string AuditLog = "AuditLog";
    public const string BudgetAuditLog = "BudgetAuditLog";
}
