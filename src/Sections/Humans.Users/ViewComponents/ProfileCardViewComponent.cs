using Humans.Email.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Models;
using Humans.Base.Helpers;
using Humans.Auth.Contracts;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.ViewComponents;

// Public: Razor's build-time tag-helper discovery only sees public view components, so an
// internal one renders <vc:profile-card> as inert markup.
public sealed class ProfileCardViewComponent(
    IUserService userService,
    IContactFieldService contactFieldService,
    IUserEmailService userEmailService,
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService,
    ICommunicationPreferenceService commPrefService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        var ct = HttpContext.RequestAborted;
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null)
        {
            return Content(string.Empty);
        }
        userId = info.Id; // everything below (emails, teams, membership) is keyed by the live id
        var profile = info.Profile;

        var isOwnProfile = viewMode == ProfileCardViewMode.Self;
        var canViewLegalName = viewMode != ProfileCardViewMode.Public;
        var viewerUserId = userId; // default for self/admin

        if (viewMode == ProfileCardViewMode.Public
            && Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
        {
            viewerUserId = viewerId;
            canViewLegalName = await roleAssignmentService.IsUserBoardMemberAsync(viewerId, ct);
        }

        var contactFields = profile is not null
            ? await contactFieldService.GetVisibleContactFieldsAsync(userId, viewerUserId, ct)
            : [];

        ContactFieldVisibility accessLevel;
        if (isOwnProfile || viewMode == ProfileCardViewMode.Admin)
        {
            accessLevel = ContactFieldVisibility.BoardOnly;
        }
        else
        {
            accessLevel = await contactFieldService.GetViewerAccessLevelAsync(userId, viewerUserId, ct);
        }
        var visibleEmails = await userEmailService.GetVisibleEmailsAsync(userId, accessLevel, ct);

        var teamsById = await teamService.GetTeamsAsync(ct);
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
                TeamName = GetDisplayName(tm.TeamInfo, teamsById, viewMode != ProfileCardViewMode.Public),
                TeamSlug = tm.TeamInfo.Slug,
                IsCoordinator = tm.Membership!.Role == TeamMemberRole.Coordinator,
                IsSystemTeam = tm.TeamInfo.IsSystemTeam
            })
            .ToList();

        var hasCustomPicture = profile?.HasCustomPicture == true;
        var pictureUrl = hasCustomPicture
            ? Url.Action(nameof(ProfileViewController.Picture), "ProfileView", new { id = profile!.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
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
            ViewMode = viewMode,
            CanViewLegalName = canViewLegalName,
            UserEmails = visibleEmails.OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase).Select(e => new UserEmailDisplayViewModel
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
            Languages = (profile?.Languages ?? [])
                .OrderByDescending(pl => pl.Proficiency)
                .ThenBy(pl => pl.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
            PreferredLanguage = info.PreferredLanguage,
            CanSendMessage = !isOwnProfile
                && !visibleEmails.Any(e => e.Visibility >= ContactFieldVisibility.AllActiveProfiles)
                && await commPrefService.AcceptsFacilitatedMessagesAsync(userId, ct)
        };

        return View(model);
    }

    private static string GetDisplayName(
        TeamInfo team,
        IReadOnlyDictionary<Guid, TeamInfo> teamsById,
        bool canSeeHiddenTeams) =>
        team.ParentTeamId is { } parentTeamId && teamsById.TryGetValue(parentTeamId, out var parent)
            && (!parent.IsHidden || canSeeHiddenTeams)
            ? $"{parent.Name} - {team.Name}"
            : team.Name;
}
