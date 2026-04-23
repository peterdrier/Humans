using Google.Apis.CloudIdentity.v1;
using Google.Apis.CloudIdentity.v1.Data;
using Google.Apis.Groupssettings.v1;
using Google.Apis.Services;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SdkGroup = Google.Apis.CloudIdentity.v1.Data.Group;
using SdkGroupSettings = Google.Apis.Groupssettings.v1.Data.Groups;
using SdkGroupSettingsBaseServiceRequest = Google.Apis.Groupssettings.v1.GroupssettingsBaseServiceRequest<Google.Apis.Groupssettings.v1.Data.Groups>;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IGoogleGroupProvisioningClient"/>.
/// Talks to the Cloud Identity Groups API (group lifecycle) and the Google
/// Workspace Groups Settings API (settings) using the configured service
/// account. This is the only file that imports <c>Google.Apis.*</c> for
/// group provisioning and settings reconciliation; the Application-layer
/// sync service (coming in §15 Part 2b) never sees SDK types.
/// </summary>
public sealed class GoogleGroupProvisioningClient : IGoogleGroupProvisioningClient
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleGroupProvisioningClient> _logger;

    private CloudIdentityService? _cloudIdentityService;
    private GroupssettingsService? _groupsSettingsService;

    public GoogleGroupProvisioningClient(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleGroupProvisioningClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<GroupCreateResult> CreateGroupAsync(
        string groupEmail,
        string displayName,
        string description,
        CancellationToken ct = default)
    {
        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            var group = new SdkGroup
            {
                GroupKey = new EntityKey { Id = groupEmail },
                DisplayName = displayName,
                Description = description,
                Parent = $"customers/{_settings.CustomerId}",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cloudidentity.googleapis.com/groups.discussion_forum"] = ""
                }
            };

            var createRequest = cloudIdentity.Groups.Create(group);
            createRequest.InitialGroupConfig =
                Google.Apis.CloudIdentity.v1.GroupsResource.CreateRequest.InitialGroupConfigEnum.WITHINITIALOWNER;
            var operation = await createRequest.ExecuteAsync(ct);

            // groups.create returns a long-running Operation. In practice
            // Cloud Identity finishes synchronously so Done is true and
            // Response is populated, but the API contract permits an
            // in-progress (Done=false, Response=null) or failed (Error set)
            // result. Treat those as a bridge-level failure so the caller
            // gets a structured GroupCreateResult with error context
            // instead of an unhandled NullReference / KeyNotFound
            // exception.
            if (operation.Error is not null)
            {
                _logger.LogWarning(
                    "Cloud Identity groups.create returned an error for {Email}: Code={Code} Message={Message}",
                    groupEmail, operation.Error.Code, operation.Error.Message);
                return new GroupCreateResult(
                    GroupNumericId: null,
                    Error: new GoogleClientError(operation.Error.Code ?? 0, operation.Error.Message));
            }

            if (operation.Done != true || operation.Response is null)
            {
                _logger.LogWarning(
                    "Cloud Identity groups.create did not complete synchronously for {Email} (Done={Done})",
                    groupEmail, operation.Done);
                return new GroupCreateResult(
                    GroupNumericId: null,
                    Error: new GoogleClientError(0, "groups.create returned an in-progress operation without a response body"));
            }

            // The resource name in the operation response is of the form
            // "groups/{id}" — strip the prefix to get the numeric id.
            if (!operation.Response.TryGetValue("name", out var nameObj) ||
                nameObj is not string resourceName ||
                !resourceName.StartsWith("groups/", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Cloud Identity groups.create response for {Email} missing or malformed 'name' field",
                    groupEmail);
                return new GroupCreateResult(
                    GroupNumericId: null,
                    Error: new GoogleClientError(0, "groups.create response did not include a 'name' field"));
            }

            var numericId = resourceName["groups/".Length..];
            return new GroupCreateResult(numericId, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error creating group {Email}: Code={Code} Message={Message}",
                groupEmail, ex.Error?.Code, ex.Error?.Message);
            return new GroupCreateResult(
                GroupNumericId: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GroupLookupIdResult> LookupGroupIdAsync(
        string groupEmail,
        CancellationToken ct = default)
    {
        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            var lookup = cloudIdentity.Groups.Lookup();
            lookup.GroupKeyId = groupEmail;
            var response = await lookup.ExecuteAsync(ct);
            var numericId = response.Name["groups/".Length..];
            return new GroupLookupIdResult(numericId, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogDebug(ex,
                "Google API error looking up group {Email}: Code={Code} Message={Message}",
                groupEmail, ex.Error?.Code, ex.Error?.Message);
            return new GroupLookupIdResult(
                GroupNumericId: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GroupSettingsGetResult> GetGroupSettingsAsync(
        string groupEmail,
        CancellationToken ct = default)
    {
        try
        {
            var groupsSettings = await GetGroupsSettingsServiceAsync(ct);
            var request = groupsSettings.Groups.Get(groupEmail);
            request.Alt = SdkGroupSettingsBaseServiceRequest.AltEnum.Json;
            var actual = await request.ExecuteAsync(ct);
            return new GroupSettingsGetResult(MapSnapshot(actual), Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error reading settings for group {Email}: Code={Code} Message={Message}",
                groupEmail, ex.Error?.Code, ex.Error?.Message);
            return new GroupSettingsGetResult(
                Settings: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GoogleClientError?> UpdateGroupSettingsAsync(
        string groupEmail,
        GroupSettingsExpected expected,
        CancellationToken ct = default)
    {
        try
        {
            var groupsSettings = await GetGroupsSettingsServiceAsync(ct);
            var sdkSettings = BuildSdkSettings(expected);
            var request = groupsSettings.Groups.Update(sdkSettings, groupEmail);
            await request.ExecuteAsync(ct);
            return null;
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error updating settings for group {Email}: Code={Code} Message={Message}",
                groupEmail, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    private static SdkGroupSettings BuildSdkSettings(GroupSettingsExpected expected) => new()
    {
        WhoCanJoin = expected.WhoCanJoin,
        WhoCanViewMembership = expected.WhoCanViewMembership,
        WhoCanContactOwner = expected.WhoCanContactOwner,
        WhoCanPostMessage = expected.WhoCanPostMessage,
        WhoCanViewGroup = expected.WhoCanViewGroup,
        WhoCanModerateMembers = expected.WhoCanModerateMembers,
        AllowExternalMembers = expected.AllowExternalMembers ? "true" : "false",
        IsArchived = expected.IsArchived ? "true" : "false",
        MembersCanPostAsTheGroup = expected.MembersCanPostAsTheGroup ? "true" : "false",
        IncludeInGlobalAddressList = expected.IncludeInGlobalAddressList ? "true" : "false",
        AllowWebPosting = expected.AllowWebPosting ? "true" : "false",
        MessageModerationLevel = expected.MessageModerationLevel,
        SpamModerationLevel = expected.SpamModerationLevel,
        EnableCollaborativeInbox = expected.EnableCollaborativeInbox ? "true" : "false"
    };

    private static GroupSettingsSnapshot MapSnapshot(SdkGroupSettings s) => new(
        WhoCanJoin: s.WhoCanJoin,
        WhoCanViewMembership: s.WhoCanViewMembership,
        WhoCanContactOwner: s.WhoCanContactOwner,
        WhoCanPostMessage: s.WhoCanPostMessage,
        WhoCanViewGroup: s.WhoCanViewGroup,
        WhoCanModerateMembers: s.WhoCanModerateMembers,
        WhoCanModerateContent: s.WhoCanModerateContent,
        WhoCanAssistContent: s.WhoCanAssistContent,
        WhoCanDiscoverGroup: s.WhoCanDiscoverGroup,
        WhoCanLeaveGroup: s.WhoCanLeaveGroup,
        AllowExternalMembers: s.AllowExternalMembers,
        AllowWebPosting: s.AllowWebPosting,
        IsArchived: s.IsArchived,
        ArchiveOnly: s.ArchiveOnly,
        MembersCanPostAsTheGroup: s.MembersCanPostAsTheGroup,
        IncludeInGlobalAddressList: s.IncludeInGlobalAddressList,
        EnableCollaborativeInbox: s.EnableCollaborativeInbox,
        MessageModerationLevel: s.MessageModerationLevel,
        SpamModerationLevel: s.SpamModerationLevel,
        ReplyTo: s.ReplyTo,
        CustomReplyTo: s.CustomReplyTo,
        IncludeCustomFooter: s.IncludeCustomFooter,
        CustomFooterText: s.CustomFooterText,
        SendMessageDenyNotification: s.SendMessageDenyNotification,
        DefaultMessageDenyNotificationText: s.DefaultMessageDenyNotificationText,
        FavoriteRepliesOnTop: s.FavoriteRepliesOnTop,
        DefaultSender: s.DefaultSender,
        PrimaryLanguage: s.PrimaryLanguage,
        WhoCanInvite: s.WhoCanInvite,
        WhoCanAdd: s.WhoCanAdd,
        ShowInGroupDirectory: s.ShowInGroupDirectory,
        AllowGoogleCommunication: s.AllowGoogleCommunication,
        WhoCanApproveMembers: s.WhoCanApproveMembers,
        WhoCanBanUsers: s.WhoCanBanUsers,
        WhoCanModifyMembers: s.WhoCanModifyMembers,
        WhoCanApproveMessages: s.WhoCanApproveMessages,
        WhoCanDeleteAnyPost: s.WhoCanDeleteAnyPost,
        WhoCanDeleteTopics: s.WhoCanDeleteTopics,
        WhoCanLockTopics: s.WhoCanLockTopics,
        WhoCanMoveTopicsIn: s.WhoCanMoveTopicsIn,
        WhoCanMoveTopicsOut: s.WhoCanMoveTopicsOut,
        WhoCanPostAnnouncements: s.WhoCanPostAnnouncements,
        WhoCanHideAbuse: s.WhoCanHideAbuse,
        WhoCanMakeTopicsSticky: s.WhoCanMakeTopicsSticky,
        WhoCanAssignTopics: s.WhoCanAssignTopics,
        WhoCanUnassignTopic: s.WhoCanUnassignTopic,
        WhoCanTakeTopics: s.WhoCanTakeTopics,
        WhoCanMarkDuplicate: s.WhoCanMarkDuplicate,
        WhoCanMarkNoResponseNeeded: s.WhoCanMarkNoResponseNeeded,
        WhoCanMarkFavoriteReplyOnAnyTopic: s.WhoCanMarkFavoriteReplyOnAnyTopic,
        WhoCanMarkFavoriteReplyOnOwnTopic: s.WhoCanMarkFavoriteReplyOnOwnTopic,
        WhoCanUnmarkFavoriteReplyOnAnyTopic: s.WhoCanUnmarkFavoriteReplyOnAnyTopic,
        WhoCanEnterFreeFormTags: s.WhoCanEnterFreeFormTags,
        WhoCanModifyTagsAndCategories: s.WhoCanModifyTagsAndCategories,
        WhoCanAddReferences: s.WhoCanAddReferences,
        MessageDisplayFont: s.MessageDisplayFont,
        MaxMessageBytes: s.MaxMessageBytes);

    private async Task<CloudIdentityService> GetCloudIdentityServiceAsync(CancellationToken ct)
    {
        if (_cloudIdentityService is not null)
        {
            return _cloudIdentityService;
        }

        var credential = await GoogleCredentialLoader
            .LoadScopedAsync(_settings, ct, CloudIdentityService.Scope.CloudIdentityGroups);

        _cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _cloudIdentityService;
    }

    private async Task<GroupssettingsService> GetGroupsSettingsServiceAsync(CancellationToken ct)
    {
        if (_groupsSettingsService is not null)
        {
            return _groupsSettingsService;
        }

        var credential = await GoogleCredentialLoader
            .LoadScopedAsync(_settings, ct, GroupssettingsService.Scope.AppsGroupsSettings);

        _groupsSettingsService = new GroupssettingsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _groupsSettingsService;
    }
}
