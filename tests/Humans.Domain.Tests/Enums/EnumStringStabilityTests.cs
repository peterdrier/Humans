using AwesomeAssertions;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Domain.Tests.Enums;

/// <summary>
/// Guards against renaming enum members that are stored as strings in the database.
/// If an enum member is renamed, the DB still has the OLD string — causing silent data mismatches.
/// When a rename IS intentional, update the expected names here AND create a DB migration
/// to UPDATE the old values.
/// </summary>
public class EnumStringStabilityTests
{
    /// <summary>
    /// Verifies that enum member names exactly match what the database stores.
    /// Renames without a corresponding DB migration will silently break queries.
    /// </summary>
    [HumansTheory]
    [MemberData(nameof(StringStoredEnumData))]
    public void StringStoredEnum_MemberNames_MustMatchExpected(
        Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);

        // Existing members must not be renamed
        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                $"If you renamed it, create a DB migration to UPDATE the old values.");
        }

        // New members are allowed (append-only), but removed members are not
        // This catches both renames (old name missing) and deletions
    }

    /// <summary>
    /// Every enum that uses HasConversion&lt;string&gt;() in EF Core configuration.
    /// Update this list when adding new string-stored enums.
    /// </summary>
    public static TheoryData<Type, string[]> StringStoredEnumData => new()
    {
        {
            typeof(TeamMemberRole), ["Member", "Coordinator"]
        },
        {
            typeof(TeamJoinRequestStatus), ["Pending", "Approved", "Rejected", "Withdrawn"]
        },
        {
            typeof(SystemTeamType), ["None", "Volunteers", "Coordinators", "Board", "Asociados", "Colaboradors"]
        },
        {
            typeof(GoogleResourceType), ["DriveFolder", "SharedDrive", "Group", "DriveFile"]
        },
        {
            typeof(ContactFieldType), ["Email", "Phone", "Signal", "Telegram", "WhatsApp", "Discord", "Other"]
        },
        {
            typeof(ContactFieldVisibility), ["BoardOnly", "CoordinatorsAndBoard", "MyTeams", "AllActiveProfiles"]
        },
        {
            typeof(AuditAction), [
                "TeamMemberAdded", "TeamMemberRemoved", "MemberSuspended", "MemberUnsuspended",
                "AccountAnonymized", "RoleAssigned", "RoleEnded", "VolunteerApproved",
                "GoogleResourceAccessGranted", "GoogleResourceAccessRevoked", "GoogleResourceProvisioned",
                "TeamJoinedDirectly", "TeamLeft", "TeamJoinRequestApproved", "TeamJoinRequestRejected",
                "TeamMemberRoleChanged", "AnomalousPermissionDetected",
                "MembershipsRevokedOnDeletionRequest", "ConsentCheckCleared", "ConsentCheckFlagged",
                "SignupRejected", "TierApplicationApproved", "TierApplicationRejected", "TierDowngraded",
                "GoogleResourceDeactivated", "FacilitatedMessageSent",
                "TeamRoleDefinitionCreated", "TeamRoleDefinitionUpdated", "TeamRoleDefinitionDeleted",
                "TeamRoleAssigned", "TeamRoleUnassigned",
                "CampCreated", "CampUpdated", "CampDeleted",
                "CampSeasonCreated", "CampSeasonApproved", "CampSeasonRejected",
                "CampSeasonWithdrawn", "CampSeasonStatusChanged", "CampNameChanged",
                "CampLeadAdded", "CampLeadRemoved", "CampPrimaryLeadTransferred",
                "CampImageUploaded", "CampImageDeleted",
                "ShiftSignupCreated", "ShiftSignupConfirmed", "ShiftSignupRefused", "ShiftSignupVoluntold",
                "ShiftSignupBailed", "ShiftSignupNoShow", "ShiftSignupCancelled", "ShiftSignupReassigned"
            ]
        },
        {
            typeof(ApplicationStatus), ["Submitted", "Approved", "Rejected", "Withdrawn"]
        },
        {
            typeof(MembershipTier), ["Volunteer", "Colaborador", "Asociado"]
        },
        {
            typeof(ConsentCheckStatus), ["Pending", "Cleared", "Flagged"]
        },
        {
            typeof(VoteChoice), ["Yay", "Maybe", "No", "Abstain"]
        },
        {
            typeof(SyncMode), ["None", "AddOnly", "AddAndRemove"]
        },
        {
            typeof(SyncServiceType), ["GoogleDrive", "GoogleGroups", "Discord"]
        },
        {
            typeof(SyncAction), ["Preview", "Execute"]
        },
        {
            typeof(TicketSyncStatus), ["Idle", "Running", "Error"]
        },
        {
            typeof(TicketPaymentStatus), ["Paid", "Pending", "Refunded"]
        },
        {
            typeof(TicketAttendeeStatus), ["Valid", "Void", "CheckedIn"]
        },
        {
            typeof(ShiftPriority), ["Normal", "Important", "Essential"]
        },
        {
            typeof(SignupPolicy), ["Public", "RequireApproval"]
        },
        {
            typeof(SignupStatus), ["Pending", "Confirmed", "Refused", "Bailed", "Cancelled", "NoShow"]
        },
        {
            typeof(RotaPeriod), ["Build", "Event", "Strike", "All"]
        },
        {
            typeof(RolePeriod), ["YearRound", "Build", "Event", "Strike"]
        },
        {
            typeof(DrivePermissionLevel), ["None", "Viewer", "Commenter", "Contributor", "ContentManager", "Manager"]
        },
        {
            typeof(BudgetYearStatus), ["Draft", "Active", "Closed"]
        },
        {
            typeof(ExpenditureType), ["CapEx", "OpEx"]
        },
        {
            typeof(MessageCategory), [
                "System", "EventOperations", "CommunityUpdates", "Marketing", "Governance",
                "CampaignCodes", "FacilitatedMessages", "Ticketing", "VolunteerUpdates", "TeamUpdates"
            ]
        }
    };
}
