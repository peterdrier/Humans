using Humans.Application.Configuration;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Single source of truth for the Google Group settings the system enforces on
/// every group it provisions. Used both at group creation (Teams + Camps
/// auto-provisioning) and by drift detection on the admin page.
/// </summary>
internal static class GroupSettingsPolicy
{
    public static GroupSettingsExpected BuildExpected(GoogleWorkspaceGroupOptions options) => new(
        WhoCanJoin: options.WhoCanJoin,
        WhoCanViewMembership: options.WhoCanViewMembership,
        WhoCanContactOwner: options.WhoCanContactOwner,
        WhoCanPostMessage: options.WhoCanPostMessage,
        WhoCanViewGroup: options.WhoCanViewGroup,
        WhoCanModerateMembers: options.WhoCanModerateMembers,
        AllowExternalMembers: options.AllowExternalMembers,
        IsArchived: true,
        MembersCanPostAsTheGroup: true,
        IncludeInGlobalAddressList: true,
        AllowWebPosting: true,
        MessageModerationLevel: "MODERATE_NONE",
        SpamModerationLevel: "MODERATE",
        EnableCollaborativeInbox: true);
}
