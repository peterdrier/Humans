using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Xunit;

namespace Humans.AuditLog.Tests.Enums;

/// <summary>
/// <see cref="AuditAction"/> is persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the OLD
/// string in every historical row, so a rename needs a migration that UPDATEs the stored values.
/// </summary>
public class EnumStringStabilityTests
{
    [HumansTheory]
    [MemberData(nameof(StringStoredEnumData))]
    public void StringStoredEnum_MemberNames_MustMatchExpected(
        Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);

        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                $"If you renamed it, create a DB migration to UPDATE the old values.");
        }
    }

    public static TheoryData<Type, string[]> StringStoredEnumData => new()
    {
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
                "ShiftSignupBailed", "ShiftSignupNoShow", "ShiftSignupCancelled", "ShiftSignupReassigned",
                // Retired: no writer left, but persisted rows still
                // carry the string, so the member has to survive.
                "CampSettingsEeStartDateChanged"
            ]
        }
    };
}
