using Humans.Application.Configuration;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Single source of truth for the enforced Google Group settings the system
/// applies on provisioning and checks during drift reconciliation.
/// </summary>
public static class GroupSettingsPolicy
{
    /// <summary>
    /// Builds the expected settings record from the configured options. The
    /// 7 dynamic fields come from <paramref name="groupOptions"/>; the
    /// remaining 6 are fixed policy values.
    /// </summary>
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
