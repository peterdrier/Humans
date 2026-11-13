using Humans.Users.Helpers;
using Humans.Email.Contracts;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Humans.Auth.Contracts;
using NodaTime;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Base.Enums;
using Humans.Teams.Contracts;

namespace Humans.Users.Services;

internal sealed class ContactFieldService(
    IUserRepository userRepository,
    IUserService userService,
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService,
    IUserInfoSliceRefresher userInfoRefresher,
    IClock clock,
    ILogger<ContactFieldService> logger,
    IStringLocalizer<UsersResource> localizer,
    IUserEmailService userEmailService,
    ICommunicationPreferenceService communicationPreferences) : IContactFieldService, IUserMerge
{
    public async Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid userId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default,
        ProfileCardViewMode viewMode = ProfileCardViewMode.Public)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info?.Profile is null)
            return [];

        var accessLevel = viewMode == ProfileCardViewMode.Public
            ? (await GetViewerAccessCoreAsync(info.Id, viewerUserId, cancellationToken)).AccessLevel
            : ContactFieldVisibility.BoardOnly;
        var allowedVisibilities = ContactFieldVisibilityRules.GetAllowedVisibilities(accessLevel);

        return info.Profile.ContactFields
            .Where(cf => allowedVisibilities.Contains(cf.Visibility))
            .Select(cf => new ContactFieldDto(
                cf.Id,
                cf.FieldType,
                cf.FieldType == ContactFieldType.Other && cf.CustomLabel is not null
                    ? cf.CustomLabel : localizer.EnumDisplay(cf.FieldType),
                cf.Value,
                cf.Visibility)
            {
                DisplayOrder = cf.DisplayOrder
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ContactFieldEditDto>> GetAllContactFieldsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var fields = await userRepository.GetByProfileIdReadOnlyAsync(profileId, cancellationToken);

        return fields.Select(cf => new ContactFieldEditDto(
            cf.Id,
            cf.FieldType,
            cf.CustomLabel,
            cf.Value,
            cf.Visibility,
            cf.DisplayOrder
        )).ToList();
    }

    public async Task<ProfileEditResult> SaveContactFieldsAsync(
        Guid profileId,
        IReadOnlyList<ContactFieldEditDto> fields,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? removedLegacyContactFieldIds = null)
    {
        if (ValidateContactFields(fields) is { } refusal)
        {
            logger.LogWarning("Contact field save refused for profile {ProfileId}: {ErrorKey}", profileId, refusal.ErrorKey);
            return refusal;
        }

        var now = clock.GetCurrentInstant();

        // Detached: mutations go back explicitly through BatchSaveAsync.
        var existingFields = await userRepository.GetByProfileIdForMutationAsync(profileId, cancellationToken);

        var existingById = existingFields.ToDictionary(cf => cf.Id);
        var incomingIds = fields.Where(f => f.Id.HasValue).Select(f => f.Id!.Value).ToHashSet();

        // Zero is the retired email type, retained only for reading historical rows.
        var toDelete = existingFields.Where(cf => cf.FieldType == default
            ? removedLegacyContactFieldIds?.Contains(cf.Id) == true
            : !incomingIds.Contains(cf.Id)).ToList();

        var toAdd = new List<ContactField>();
        var toUpdate = new List<ContactField>();

        foreach (var dto in fields)
        {
            ContactField field;
            if (dto.Id.HasValue && existingById.TryGetValue(dto.Id.Value, out var existing))
            {
                if (existing.FieldType == default) continue;
                field = existing;
                toUpdate.Add(field);
            }
            else
            {
                field = new ContactField
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    CreatedAt = now
                };
                toAdd.Add(field);
            }

            field.FieldType = dto.FieldType;
            field.CustomLabel = dto.CustomLabel;
            field.Value = dto.Value;
            field.Visibility = dto.Visibility;
            field.DisplayOrder = dto.DisplayOrder;
            field.UpdatedAt = now;
        }

        // Resolve the cache key before committing; a failed lookup must not skip refresh.
        var ownerUserId = await userRepository.GetOwnerUserIdAsync(profileId, cancellationToken);
        try
        {
            await userRepository.BatchSaveAsync(toAdd, toUpdate, toDelete, cancellationToken);
        }
        finally
        {
            if (ownerUserId is not null)
            {
                try
                {
                    // The write may have committed even if its acknowledgement failed. Finish the refresh
                    // independently of the request so old contact values and visibility cannot remain cached.
                    await userInfoRefresher.RefreshAsync(ownerUserId.Value, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "UserInfo refresh failed after contact field write for {UserId}",
                        ownerUserId.Value);
                }
            }
        }
        return new(true, profileId);
    }

    internal static bool IsValidPhone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) || phone.TrimStart().StartsWith("+", StringComparison.Ordinal);

    internal static ProfileEditResult? ValidateContactFields(IReadOnlyList<ContactFieldEditDto>? fields)
    {
        if (fields is null) return null;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (field.FieldType is ContactFieldType.Phone or ContactFieldType.WhatsApp && !IsValidPhone(field.Value))
                return new(false, ErrorKey: "Validation_PhoneE164", ErrorField: $"ContactFields[{i}].Value",
                    ErrorArgumentKey: "Users_Profile_" + field.FieldType);
        }
        return null;
    }

    public async Task<(ContactFieldVisibility AccessLevel, bool CanViewLegalName, bool CanSendMessage)> GetViewerAccessLevelAsync(
        Guid ownerUserId, Guid viewerUserId, CancellationToken cancellationToken = default,
        ProfileCardViewMode viewMode = ProfileCardViewMode.Public)
    {
        var access = viewMode == ProfileCardViewMode.Public
            ? await GetViewerAccessCoreAsync(ownerUserId, viewerUserId, cancellationToken)
            : (AccessLevel: ContactFieldVisibility.BoardOnly, CanViewLegalName: true);
        var visibleEmails = await userEmailService.GetVisibleEmailsAsync(ownerUserId, access.AccessLevel, cancellationToken);
        var canSendMessage = viewMode != ProfileCardViewMode.Self
            && !visibleEmails.Any(e => e.Visibility >= ContactFieldVisibility.AllActiveProfiles)
            && await communicationPreferences.AcceptsFacilitatedMessagesAsync(ownerUserId, cancellationToken);
        return (access.AccessLevel, access.CanViewLegalName, canSendMessage);
    }

    private async Task<(ContactFieldVisibility AccessLevel, bool CanViewLegalName)> GetViewerAccessCoreAsync(
        Guid ownerUserId, Guid viewerUserId, CancellationToken cancellationToken)
    {
        var isBoard = await roleAssignmentService.IsUserBoardMemberAsync(viewerUserId, cancellationToken);
        if (ownerUserId == viewerUserId || isBoard)
            return (ContactFieldVisibility.BoardOnly, isBoard);

        // Permissions belong to this viewer, not to the scoped service instance.
        var allTeams = (await teamService.GetTeamsAsync(cancellationToken)).Values;
        var viewerMemberships = allTeams
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == viewerUserId) })
            .Where(x => x.Membership is not null)
            .ToList();
        if (viewerMemberships.Any(x => x.Membership!.Role == TeamMemberRole.Coordinator))
            return (ContactFieldVisibility.CoordinatorsAndBoard, false);

        var viewerTeamIds = viewerMemberships
            .Where(x => x.TeamInfo.SystemTeamType != SystemTeamType.Volunteers)
            .Select(x => x.TeamInfo.Id)
            .ToHashSet();

        // Shared team excluding Volunteers, from the same team snapshot as the viewer.
        var ownerTeamIds = allTeams
            .Where(t => t.SystemTeamType != SystemTeamType.Volunteers
                     && t.Members.Any(m => m.UserId == ownerUserId))
            .Select(t => t.Id);

        if (viewerTeamIds.Overlaps(ownerTeamIds))
            return (ContactFieldVisibility.MyTeams, false);

        return (ContactFieldVisibility.AllActiveProfiles, false);
    }

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
        => userRepository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, cancellationToken);
}
