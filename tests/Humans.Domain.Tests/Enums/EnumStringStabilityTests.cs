using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Domain.Enums;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
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
    /// <remarks>
    /// G5 lane 4b-2k moved seven of these enums out of <c>Humans.Domain.Enums</c> and onto their
    /// owning sections' contracts leaves — <c>TeamMemberRole</c>, <c>TeamJoinRequestStatus</c> and
    /// <c>RolePeriod</c> to <c>Humans.Teams.Contracts</c>; <c>ShiftPriority</c>,
    /// <c>SignupPolicy</c>, <c>SignupStatus</c> and <c>RotaPeriod</c> to
    /// <c>Humans.Shifts.Contracts</c>. The rows stay here rather than splitting into per-section
    /// test projects (Tickets' shape, above) because every one of them is still public on a leaf
    /// this project can name, so the guard keeps working unsplit. Member names are unchanged, so
    /// no persisted string moved.
    ///
    /// G5 lane 4b-2l moved an eighth — <c>AuditAction</c> to <c>Humans.AuditLog.Contracts</c> —
    /// on the same terms.
    ///
    /// G5 lane 3b emptied and deleted <c>src/Humans.Domain</c>. Four of the rows below moved with
    /// it: <c>MembershipTier</c>, <c>ConsentCheckStatus</c> and <c>MessageCategory</c> to
    /// <c>Humans.Users.Contracts</c>, and <c>SyncAction</c> to
    /// <c>Humans.GoogleIntegration.Contracts</c> beside its <c>GoogleResourceType</c> and
    /// <c>DrivePermissionLevel</c> siblings. Member names are unchanged in every case, so no
    /// persisted string moved and no migration is involved — which is exactly what the rows below
    /// assert. <c>SystemTeamType</c> and <c>EmailOutboxStatus</c> keep the
    /// <c>Humans.Domain.Enums</c> namespace and now live in the <c>Humans.Interfaces</c> assembly:
    /// <c>EmailOutboxStatus</c> because Campaigns' <c>campaign_grants</c> and Surveys'
    /// <c>survey_invitations</c> persist it alongside Email's own outbox, so it fails the "who
    /// writes it to a table" test for a single owner (design §15 step 5, Email).
    /// </remarks>
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
            typeof(MembershipTier), ["Volunteer", "Colaborador", "Asociado"]
        },
        {
            typeof(ConsentCheckStatus), ["Pending", "Cleared", "Flagged"]
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
        // Tickets' three string-stored enums moved to
        // tests/Humans.Tickets.Tests/Enums/TicketEnumStringStabilityTests.cs at that
        // section's G5 move — two are internal to Humans.Tickets and one pair is on its
        // contracts leaf, so this project can no longer name them (Budget's rule).
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
            typeof(MessageCategory), [
                "System", "EventOperations", "CommunityUpdates", "Marketing", "Governance",
                "CampaignCodes", "FacilitatedMessages", "Ticketing", "VolunteerUpdates", "TeamUpdates"
            ]
        },
        {
            // Guarded centrally rather than per section: three sections persist this one
            // public Humans.Domain enum as a string — campaign_grants.LatestEmailStatus
            // (CampaignGrantConfiguration), survey_invitations.LatestEmailStatus
            // (SurveyInvitationConfiguration), and the Email outbox's own Status
            // (EmailOutboxMessageConfiguration). A rename strands the old string in all three.
            typeof(EmailOutboxStatus), ["Queued", "Sent", "Failed"]
        }
    };
}
