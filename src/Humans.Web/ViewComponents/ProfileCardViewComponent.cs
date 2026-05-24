using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Web.ViewComponents;

public enum ProfileCardViewMode
{
    Self,
    Public,
    Admin
}

public class ProfileCardViewComponent(
    IUserServiceRead userService,
    IContactFieldService contactFieldService,
    IUserEmailService userEmailService,
    ITeamServiceRead teamService,
    IRoleAssignmentService roleAssignmentService,
    IMembershipCalculator membershipCalculator,
    ICommunicationPreferenceService commPrefService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        var info = await userService.GetUserInfoAsync(userId);
        if (info is null)
        {
            return Content(string.Empty);
        }
        var profile = info.Profile;

        var isOwnProfile = viewMode == ProfileCardViewMode.Self;
        var canViewLegalName = viewMode != ProfileCardViewMode.Public;
        var viewerUserId = userId; // default for self/admin

        // For public view, resolve actual permissions based on viewer
        if (viewMode == ProfileCardViewMode.Public
            && Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
        {
            viewerUserId = viewerId;
            canViewLegalName = await roleAssignmentService.IsUserBoardMemberAsync(viewerId);
        }

        var contactFields = profile is not null
            ? await contactFieldService.GetVisibleContactFieldsAsync(userId, viewerUserId)
            : [];

        // Get visible user emails based on access level
        ContactFieldVisibility accessLevel;
        if (isOwnProfile || viewMode == ProfileCardViewMode.Admin)
        {
            accessLevel = ContactFieldVisibility.BoardOnly;
        }
        else
        {
            accessLevel = await contactFieldService.GetViewerAccessLevelAsync(userId, viewerUserId);
        }
        var visibleEmails = await userEmailService.GetVisibleEmailsAsync(userId, accessLevel);

        var teamsById = await teamService.GetTeamsAsync();
        var displayableTeams = teamsById
            .Values
            .Where(t => t.IsActive
                && t.SystemTeamType != SystemTeamType.Volunteers
                && (viewMode != ProfileCardViewMode.Public || !t.IsHidden))
            .Select(t => new
            {
                TeamInfo = t,
                Membership = t.Members.FirstOrDefault(m => m.UserId == userId)
            })
            .Where(tm => tm.Membership is not null)
            .OrderBy(tm => tm.TeamInfo.Name, StringComparer.Ordinal)
            .Select(tm => new TeamMembershipViewModel
            {
                TeamId = tm.TeamInfo.Id,
                TeamName = GetDisplayName(tm.TeamInfo, teamsById),
                TeamSlug = tm.TeamInfo.Slug,
                IsCoordinator = tm.Membership!.Role == TeamMemberRole.Coordinator,
                IsSystemTeam = tm.TeamInfo.IsSystemTeam
            })
            .ToList();

        var membershipSnapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId);

        var hasCustomPicture = profile?.HasCustomPicture == true;
        var pictureUrl = hasCustomPicture
            ? Url.Action(nameof(ProfileController.Picture), "Profile", new { id = profile!.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
            : null;

        var model = new ProfileCardViewModel
        {
            UserId = userId,
            DisplayName = info.BurnerName,
            ProfilePictureUrl = info.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = pictureUrl,
            BurnerName = profile?.BurnerName ?? string.Empty,
            Pronouns = profile?.Pronouns,
            MembershipStatus = membershipSnapshot.Status,
            IsApproved = profile?.IsApproved ?? false,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            BirthdayMonth = profile?.BirthdayMonth,
            BirthdayDay = profile?.BirthdayDay,
            Bio = profile?.Bio,
            ContributionInterests = profile?.ContributionInterests,
            BoardNotes = canViewLegalName ? profile?.BoardNotes : null,
            FirstName = canViewLegalName ? profile?.FirstName : null,
            LastName = canViewLegalName ? profile?.LastName : null,
            EmergencyContactName = canViewLegalName ? profile?.EmergencyContactName : null,
            EmergencyContactPhone = canViewLegalName ? profile?.EmergencyContactPhone : null,
            EmergencyContactRelationship = canViewLegalName ? profile?.EmergencyContactRelationship : null,
            HasPendingConsents = membershipSnapshot.PendingConsentCount > 0,
            PendingConsentCount = membershipSnapshot.PendingConsentCount,
            ViewMode = viewMode,
            CanViewLegalName = canViewLegalName,
            UserEmails = visibleEmails.Select(e => new UserEmailDisplayViewModel
            {
                Email = e.Email,
                IsPrimary = e.IsPrimary,
                Visibility = e.Visibility
            }).ToList(),
            ContactFields = contactFields.Select(cf => new ContactFieldViewModel
            {
                Id = cf.Id,
                FieldType = cf.FieldType,
                Label = cf.Label,
                Value = cf.Value,
                Visibility = cf.Visibility
            }).ToList(),
            VolunteerHistory = (profile?.VolunteerHistory ?? []).Select(cv => new VolunteerHistoryEntryViewModel
            {
                Date = cv.Date,
                EventName = cv.EventName,
                Description = cv.Description
            }).ToList(),
            Teams = displayableTeams,
            Languages = (profile?.Languages ?? []).Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
            PreferredLanguage = info.PreferredLanguage,
            CanSendMessage = !isOwnProfile
                && !visibleEmails.Any(e => e.Visibility >= ContactFieldVisibility.AllActiveProfiles)
                && await commPrefService.AcceptsFacilitatedMessagesAsync(userId)
        };

        return View(model);
    }

    private static string GetDisplayName(
        TeamInfo team,
        IReadOnlyDictionary<Guid, TeamInfo> teamsById) =>
        team.ParentTeamId is { } parentTeamId && teamsById.TryGetValue(parentTeamId, out var parent)
            ? $"{parent.Name} - {team.Name}"
            : team.Name;
}
