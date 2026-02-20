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
            new[] { "Member", "Lead" }
        },
        {
            typeof(TeamJoinRequestStatus),
            new[] { "Pending", "Approved", "Rejected", "Withdrawn" }
        },
        {
            typeof(SystemTeamType),
            new[] { "None", "Volunteers", "Leads", "Board", "Asociados", "Colaboradors" }
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
            new[] { "BoardOnly", "LeadsAndBoard", "MyTeams", "AllActiveProfiles" }
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
                "SignupRejected", "TierApplicationApproved", "TierApplicationRejected", "TierDowngraded"
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
        }
    };
}
