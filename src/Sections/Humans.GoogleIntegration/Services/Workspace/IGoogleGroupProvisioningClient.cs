namespace Humans.GoogleIntegration.Services.Workspace;

internal interface IGoogleGroupProvisioningClient
{
    /// <summary>Returns the numeric id, the <c>{id}</c> in <c>groups/{id}</c>.</summary>
    Task<GroupCreateResult> CreateGroupAsync(
        string groupEmail,
        string displayName,
        string description,
        CancellationToken ct = default);

    /// <summary>A missing group is an error (404 or 403).</summary>
    Task<GroupLookupIdResult> LookupGroupIdAsync(
        string groupEmail,
        CancellationToken ct = default);

    /// <summary>The snapshot includes deprecated settings, for the all-groups page.</summary>
    Task<GroupSettingsGetResult> GetGroupSettingsAsync(
        string groupEmail,
        CancellationToken ct = default);

    /// <summary>One <c>Groups.Update</c> call; null on success.</summary>
    Task<GoogleClientError?> UpdateGroupSettingsAsync(
        string groupEmail,
        GroupSettingsExpected expected,
        CancellationToken ct = default);
}

/// <summary>Exactly one of <see cref="GroupNumericId"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record GroupCreateResult(string? GroupNumericId, GoogleClientError? Error);

/// <summary>Exactly one of <see cref="GroupNumericId"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record GroupLookupIdResult(string? GroupNumericId, GoogleClientError? Error);

/// <summary>Exactly one of <see cref="Settings"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record GroupSettingsGetResult(GroupSettingsSnapshot? Settings, GoogleClientError? Error);

/// <summary>
/// Every field is nullable: the Settings API returns null for settings never set. Deprecated fields are shown on the
/// all-groups page but not reconciled.
/// </summary>
internal sealed record GroupSettingsSnapshot(
    string? WhoCanJoin,
    string? WhoCanViewMembership,
    string? WhoCanContactOwner,
    string? WhoCanPostMessage,
    string? WhoCanViewGroup,
    string? WhoCanModerateMembers,
    string? WhoCanModerateContent,
    string? WhoCanAssistContent,
    string? WhoCanDiscoverGroup,
    string? WhoCanLeaveGroup,
    string? AllowExternalMembers,
    string? AllowWebPosting,
    string? IsArchived,
    string? ArchiveOnly,
    string? MembersCanPostAsTheGroup,
    string? IncludeInGlobalAddressList,
    string? EnableCollaborativeInbox,
    string? MessageModerationLevel,
    string? SpamModerationLevel,
    string? ReplyTo,
    string? CustomReplyTo,
    string? IncludeCustomFooter,
    string? CustomFooterText,
    string? SendMessageDenyNotification,
    string? DefaultMessageDenyNotificationText,
    string? FavoriteRepliesOnTop,
    string? DefaultSender,
    string? PrimaryLanguage,
    // Deprecated settings (still returned by API; surfaced for visibility only)
    string? WhoCanInvite,
    string? WhoCanAdd,
    string? ShowInGroupDirectory,
    string? AllowGoogleCommunication,
    string? WhoCanApproveMembers,
    string? WhoCanBanUsers,
    string? WhoCanModifyMembers,
    string? WhoCanApproveMessages,
    string? WhoCanDeleteAnyPost,
    string? WhoCanDeleteTopics,
    string? WhoCanLockTopics,
    string? WhoCanMoveTopicsIn,
    string? WhoCanMoveTopicsOut,
    string? WhoCanPostAnnouncements,
    string? WhoCanHideAbuse,
    string? WhoCanMakeTopicsSticky,
    string? WhoCanAssignTopics,
    string? WhoCanUnassignTopic,
    string? WhoCanTakeTopics,
    string? WhoCanMarkDuplicate,
    string? WhoCanMarkNoResponseNeeded,
    string? WhoCanMarkFavoriteReplyOnAnyTopic,
    string? WhoCanMarkFavoriteReplyOnOwnTopic,
    string? WhoCanUnmarkFavoriteReplyOnAnyTopic,
    string? WhoCanEnterFreeFormTags,
    string? WhoCanModifyTagsAndCategories,
    string? WhoCanAddReferences,
    string? MessageDisplayFont,
    long? MaxMessageBytes);

/// <summary>The enforced subset of <see cref="GroupSettingsSnapshot"/>.</summary>
internal sealed record GroupSettingsExpected(
    string? WhoCanJoin,
    string? WhoCanViewMembership,
    string? WhoCanContactOwner,
    string? WhoCanPostMessage,
    string? WhoCanViewGroup,
    string? WhoCanModerateMembers,
    bool AllowExternalMembers,
    bool IsArchived,
    bool MembersCanPostAsTheGroup,
    bool IncludeInGlobalAddressList,
    bool AllowWebPosting,
    string MessageModerationLevel,
    string SpamModerationLevel,
    bool EnableCollaborativeInbox);
