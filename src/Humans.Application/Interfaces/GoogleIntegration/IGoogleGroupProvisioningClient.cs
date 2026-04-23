namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Narrow connector over the Google Cloud Identity Groups API and the Groups
/// Settings API, scoped to the group-lifecycle and configuration operations
/// performed by <c>GoogleWorkspaceSyncService</c>. Implementations live in
/// <c>Humans.Infrastructure</c>; the Application-layer sync service (coming
/// in §15 Part 2b, issue #575) depends only on this interface so that
/// <c>Humans.Application</c> stays free of <c>Google.Apis.*</c> imports
/// (design-rules §13).
/// </summary>
public interface IGoogleGroupProvisioningClient
{
    /// <summary>
    /// Creates a new Google Group with the given email and display name under
    /// the configured Cloud Identity customer and returns its numeric id.
    /// </summary>
    /// <param name="groupEmail">Primary email of the new group.</param>
    /// <param name="displayName">Display name of the new group.</param>
    /// <param name="description">Description shown on the group page.</param>
    /// <returns>
    /// The numeric group id (the <c>{id}</c> in <c>groups/{id}</c>) on
    /// success, or a populated <see cref="GoogleClientError"/> on failure.
    /// </returns>
    Task<GroupCreateResult> CreateGroupAsync(
        string groupEmail,
        string displayName,
        string description,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up a Google Group by its email and returns its numeric id.
    /// Returns a populated <see cref="GoogleClientError"/> when the group
    /// does not exist (HTTP 404 / 403) or any other failure occurs.
    /// </summary>
    Task<GroupLookupIdResult> LookupGroupIdAsync(
        string groupEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the current Google Workspace group settings for the group
    /// identified by <paramref name="groupEmail"/>.
    /// </summary>
    /// <returns>
    /// A shape-neutral <see cref="GroupSettingsSnapshot"/> on success, or a
    /// populated <see cref="GoogleClientError"/> on failure. The snapshot
    /// surfaces both the enforced settings and the deprecated ones so the
    /// admin "all groups" page can display them.
    /// </returns>
    Task<GroupSettingsGetResult> GetGroupSettingsAsync(
        string groupEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the supplied <paramref name="expected"/> settings to the group.
    /// The implementation issues a single <c>Groups.Update</c> call, returning
    /// null on success or a populated <see cref="GoogleClientError"/> when
    /// Google rejects the update.
    /// </summary>
    Task<GoogleClientError?> UpdateGroupSettingsAsync(
        string groupEmail,
        GroupSettingsExpected expected,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IGoogleGroupProvisioningClient.CreateGroupAsync"/>.
/// Exactly one of <see cref="GroupNumericId"/> or <see cref="Error"/> is
/// non-null.
/// </summary>
public sealed record GroupCreateResult(string? GroupNumericId, GoogleClientError? Error);

/// <summary>
/// Outcome of <see cref="IGoogleGroupProvisioningClient.LookupGroupIdAsync"/>.
/// Exactly one of <see cref="GroupNumericId"/> or <see cref="Error"/> is
/// non-null.
/// </summary>
public sealed record GroupLookupIdResult(string? GroupNumericId, GoogleClientError? Error);

/// <summary>
/// Outcome of <see cref="IGoogleGroupProvisioningClient.GetGroupSettingsAsync"/>.
/// Exactly one of <see cref="Settings"/> or <see cref="Error"/> is non-null.
/// </summary>
public sealed record GroupSettingsGetResult(GroupSettingsSnapshot? Settings, GoogleClientError? Error);

/// <summary>
/// Complete shape-neutral snapshot of a Google Group's settings, including
/// both enforced and deprecated fields (deprecated fields are shown on the
/// admin all-groups page for visibility even though they aren't reconciled).
/// Every field is nullable because the Groups Settings API returns null for
/// settings that haven't been explicitly set.
/// </summary>
public sealed record GroupSettingsSnapshot(
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

/// <summary>
/// Expected Google Group settings the caller wants applied. Mirrors the
/// enforced subset of <see cref="GroupSettingsSnapshot"/> — the settings the
/// system reconciles on provisioning and drift checks. The connector
/// translates these into Google SDK types internally.
/// </summary>
public sealed record GroupSettingsExpected(
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
