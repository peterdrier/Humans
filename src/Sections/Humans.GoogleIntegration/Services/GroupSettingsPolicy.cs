using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services.Workspace;

using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Single source of truth for the enforced Google Group settings the system
/// applies on provisioning and checks during drift reconciliation.
/// </summary>
internal static class GroupSettingsPolicy
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
