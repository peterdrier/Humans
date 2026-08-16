using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Xunit;

namespace Humans.AuditLog.Tests.Enums;

/// <summary>
/// AuditLog's share of the string-stored-enum guard, moved out of
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> when that orphaned project's rows
/// were distributed to their owners —
/// <see cref="AuditAction"/> lives on <c>Humans.AuditLog.Contracts</c>, so the guard belongs to
/// the section that owns it (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// <c>AuditAction</c> is persisted with <c>HasConversion&lt;string&gt;()</c> in
/// <c>audit_log_entries.action</c>: renaming a member leaves the OLD string in the column, and
/// every historical row keeps it. A rename needs a migration that UPDATEs the stored values.
/// </remarks>
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
                "ShiftSignupBailed", "ShiftSignupNoShow", "ShiftSignupCancelled", "ShiftSignupReassigned"
            ]
        }
    };
}
