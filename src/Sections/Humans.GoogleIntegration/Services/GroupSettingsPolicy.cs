using Humans.GoogleIntegration.Services.Workspace;

using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>Applied on provisioning and checked for drift.</summary>
internal static class GroupSettingsPolicy
{
    public static GroupSettingsExpected BuildExpected(GoogleWorkspaceGroupOptions groupOptions) => new(
        WhoCanJoin: groupOptions.WhoCanJoin,
        WhoCanViewMembership: groupOptions.WhoCanViewMembership,
        WhoCanContactOwner: groupOptions.WhoCanContactOwner,
        WhoCanPostMessage: groupOptions.WhoCanPostMessage,
        WhoCanViewGroup: groupOptions.WhoCanViewGroup,
        WhoCanModerateMembers: groupOptions.WhoCanModerateMembers,
        AllowExternalMembers: groupOptions.AllowExternalMembers,
        IsArchived: true,
        MembersCanPostAsTheGroup: true,
        IncludeInGlobalAddressList: true,
        AllowWebPosting: true,
        MessageModerationLevel: "MODERATE_NONE",
        SpamModerationLevel: "MODERATE",
        EnableCollaborativeInbox: true);
}
