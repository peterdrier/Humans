using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Dev/test <see cref="IGoogleGroupProvisioningClient"/> that keeps an
/// in-memory map of provisioned groups so the Application-layer sync service
/// can exercise group lifecycle flows without a Google service account.
/// Per the §15 connector pattern, the Application-layer service runs
/// against this stub — there is no "stub service" variant.
/// </summary>
public sealed class StubGoogleGroupProvisioningClient : IGoogleGroupProvisioningClient
{
    private readonly ILogger<StubGoogleGroupProvisioningClient> _logger;
    private readonly Dictionary<string, StubGroup> _groupsByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupSettingsSnapshot> _settingsByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _nextGroupId = 1;

    public StubGoogleGroupProvisioningClient(ILogger<StubGoogleGroupProvisioningClient> logger)
    {
        _logger = logger;
    }

    public Task<GroupCreateResult> CreateGroupAsync(
        string groupEmail,
        string displayName,
        string description,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Create group {Email} '{Name}'", groupEmail, displayName);

        lock (_gate)
        {
            if (_groupsByEmail.ContainsKey(groupEmail))
            {
                return Task.FromResult(new GroupCreateResult(
                    GroupNumericId: null,
                    Error: new GoogleClientError(409, "group already exists")));
            }

            var id = $"stubgroup-{_nextGroupId++}";
            _groupsByEmail[groupEmail] = new StubGroup(id, displayName);
            return Task.FromResult(new GroupCreateResult(id, Error: null));
        }
    }

    public Task<GroupLookupIdResult> LookupGroupIdAsync(
        string groupEmail,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] Lookup group {Email}", groupEmail);

        lock (_gate)
        {
            if (_groupsByEmail.TryGetValue(groupEmail, out var group))
            {
                return Task.FromResult(new GroupLookupIdResult(group.Id, Error: null));
            }
        }

        return Task.FromResult(new GroupLookupIdResult(
            GroupNumericId: null,
            Error: new GoogleClientError(404, "group not found")));
    }

    public Task<GroupSettingsGetResult> GetGroupSettingsAsync(
        string groupEmail,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] Get group settings for {Email}", groupEmail);

        lock (_gate)
        {
            if (_settingsByEmail.TryGetValue(groupEmail, out var snapshot))
            {
                return Task.FromResult(new GroupSettingsGetResult(snapshot, Error: null));
            }
        }

        return Task.FromResult(new GroupSettingsGetResult(
            Settings: null,
            Error: new GoogleClientError(404, "group settings not found")));
    }

    public Task<GoogleClientError?> UpdateGroupSettingsAsync(
        string groupEmail,
        GroupSettingsExpected expected,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Update settings for {Email}", groupEmail);

        var snapshot = MapSnapshot(expected);
        lock (_gate)
        {
            _settingsByEmail[groupEmail] = snapshot;
        }
        return Task.FromResult<GoogleClientError?>(null);
    }

    /// <summary>
    /// Builds a shape-neutral snapshot from the expected-settings input, so
    /// a subsequent <see cref="GetGroupSettingsAsync"/> call returns what was
    /// written. Fields not present on <see cref="GroupSettingsExpected"/> are
    /// left null (the deprecated + ancillary fields the admin page displays
    /// for visibility only — not reconciled).
    /// </summary>
    private static GroupSettingsSnapshot MapSnapshot(GroupSettingsExpected e) => new(
        WhoCanJoin: e.WhoCanJoin,
        WhoCanViewMembership: e.WhoCanViewMembership,
        WhoCanContactOwner: e.WhoCanContactOwner,
        WhoCanPostMessage: e.WhoCanPostMessage,
        WhoCanViewGroup: e.WhoCanViewGroup,
        WhoCanModerateMembers: e.WhoCanModerateMembers,
        WhoCanModerateContent: null,
        WhoCanAssistContent: null,
        WhoCanDiscoverGroup: null,
        WhoCanLeaveGroup: null,
        AllowExternalMembers: e.AllowExternalMembers ? "true" : "false",
        AllowWebPosting: e.AllowWebPosting ? "true" : "false",
        IsArchived: e.IsArchived ? "true" : "false",
        ArchiveOnly: null,
        MembersCanPostAsTheGroup: e.MembersCanPostAsTheGroup ? "true" : "false",
        IncludeInGlobalAddressList: e.IncludeInGlobalAddressList ? "true" : "false",
        EnableCollaborativeInbox: e.EnableCollaborativeInbox ? "true" : "false",
        MessageModerationLevel: e.MessageModerationLevel,
        SpamModerationLevel: e.SpamModerationLevel,
        ReplyTo: null,
        CustomReplyTo: null,
        IncludeCustomFooter: null,
        CustomFooterText: null,
        SendMessageDenyNotification: null,
        DefaultMessageDenyNotificationText: null,
        FavoriteRepliesOnTop: null,
        DefaultSender: null,
        PrimaryLanguage: null,
        WhoCanInvite: null,
        WhoCanAdd: null,
        ShowInGroupDirectory: null,
        AllowGoogleCommunication: null,
        WhoCanApproveMembers: null,
        WhoCanBanUsers: null,
        WhoCanModifyMembers: null,
        WhoCanApproveMessages: null,
        WhoCanDeleteAnyPost: null,
        WhoCanDeleteTopics: null,
        WhoCanLockTopics: null,
        WhoCanMoveTopicsIn: null,
        WhoCanMoveTopicsOut: null,
        WhoCanPostAnnouncements: null,
        WhoCanHideAbuse: null,
        WhoCanMakeTopicsSticky: null,
        WhoCanAssignTopics: null,
        WhoCanUnassignTopic: null,
        WhoCanTakeTopics: null,
        WhoCanMarkDuplicate: null,
        WhoCanMarkNoResponseNeeded: null,
        WhoCanMarkFavoriteReplyOnAnyTopic: null,
        WhoCanMarkFavoriteReplyOnOwnTopic: null,
        WhoCanUnmarkFavoriteReplyOnAnyTopic: null,
        WhoCanEnterFreeFormTags: null,
        WhoCanModifyTagsAndCategories: null,
        WhoCanAddReferences: null,
        MessageDisplayFont: null,
        MaxMessageBytes: null);

    private sealed record StubGroup(string Id, string DisplayName);
}
