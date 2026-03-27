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
    [Theory]
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
            typeof(TeamMemberRole),
            new[] { "Member", "Coordinator" }
        },
        {
            typeof(TeamJoinRequestStatus),
            new[] { "Pending", "Approved", "Rejected", "Withdrawn" }
        },
        {
            typeof(SystemTeamType),
            new[] { "None", "Volunteers", "Coordinators", "Board", "Asociados", "Colaboradors" }
        },
        {
            typeof(GoogleResourceType),
            new[] { "DriveFolder", "SharedDrive", "Group", "DriveFile" }
        },
        {
            typeof(ContactFieldType),
            new[] { "Email", "Phone", "Signal", "Telegram", "WhatsApp", "Discord", "Other" }
        },
        {
            typeof(ContactFieldVisibility),
            new[] { "BoardOnly", "CoordinatorsAndBoard", "MyTeams", "AllActiveProfiles" }
        },
        {
            typeof(AuditAction),
            new[]
            {
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
                "ShiftSignupConfirmed", "ShiftSignupRefused", "ShiftSignupVoluntold",
                "ShiftSignupBailed", "ShiftSignupNoShow", "ShiftSignupCancelled"
            }
        },
        {
            typeof(ApplicationStatus),
            new[] { "Submitted", "Approved", "Rejected", "Withdrawn" }
        },
        {
            typeof(MembershipTier),
            new[] { "Volunteer", "Colaborador", "Asociado" }
        },
        {
            typeof(ConsentCheckStatus),
            new[] { "Pending", "Cleared", "Flagged" }
        },
        {
            typeof(VoteChoice),
            new[] { "Yay", "Maybe", "No", "Abstain" }
        },
        {
            typeof(SyncMode),
            new[] { "None", "AddOnly", "AddAndRemove" }
        },
        {
            typeof(SyncServiceType),
            new[] { "GoogleDrive", "GoogleGroups", "Discord" }
        },
        {
            typeof(SyncAction),
            new[] { "Preview", "Execute" }
        },
        {
            typeof(TicketSyncStatus),
            new[] { "Idle", "Running", "Error" }
        },
        {
            typeof(TicketPaymentStatus),
            new[] { "Paid", "Pending", "Refunded" }
        },
        {
            typeof(TicketAttendeeStatus),
            new[] { "Valid", "Void", "CheckedIn" }
        },
        {
            typeof(ShiftPriority),
            new[] { "Normal", "Important", "Essential" }
        },
        {
            typeof(SignupPolicy),
            new[] { "Public", "RequireApproval" }
        },
        {
            typeof(SignupStatus),
            new[] { "Pending", "Confirmed", "Refused", "Bailed", "Cancelled", "NoShow" }
        },
        {
            typeof(RotaPeriod),
            new[] { "Build", "Event", "Strike", "All" }
        },
        {
            typeof(RolePeriod),
            new[] { "YearRound", "Build", "Event", "Strike" }
        },
        {
            typeof(DrivePermissionLevel),
            new[] { "Viewer", "Commenter", "Contributor", "ContentManager", "Manager" }
        },
        {
            typeof(BudgetYearStatus),
            new[] { "Draft", "Active", "Closed" }
        },
        {
            typeof(ExpenditureType),
            new[] { "CapEx", "OpEx" }
        }
    };
}
