using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Models;
using Humans.Base.Helpers;
using Humans.Auth.Contracts;
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
    ISystemRoleService systemRoleService,
    ICommunicationPreferenceService commPrefService) : ViewComponent
{
    // Default is the whole card (/Profile/Me, /Profile/{id}); /Users/Me splits it into
    // Identity (left column), Teams (a tile) and History (Burner CV).
    private static readonly HashSet<string> Layouts = new(StringComparer.Ordinal) { "Default", "Identity", "Board", "Teams", "History" };

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode, string layout = "Default")
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

        if (viewMode == ProfileCardViewMode.Public)
        {
            if (UserClaimsPrincipal.GetUserId() is not { } viewerId)
                return Content(string.Empty);
            var viewer = await userService.GetUserInfoAsync(viewerId, ct);
            if (viewer is null) return Content(string.Empty);

            viewerUserId = viewer.Id;
            canViewLegalName = await systemRoleService.IsUserBoardMemberAsync(viewerUserId, ct);
        }

        var contactFields = profile is not null
            ? await contactFieldService.GetVisibleContactFieldsAsync(userId, viewerUserId, ct)
            : [];

        var accessLevel = await contactFieldService.GetViewerAccessLevelAsync(userId, viewerUserId, ct);
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
                TeamName = GetDisplayName(tm.TeamInfo, teamsById, viewMode != ProfileCardViewMode.Public),
                TeamSlug = tm.TeamInfo.Slug,
                IsCoordinator = tm.Membership!.Role == TeamMemberRole.Coordinator,
                IsSystemTeam = tm.TeamInfo.IsSystemTeam
            })
            .ToList();

        var model = new ProfileCardViewModel
        {
            UserId = userId,
            Pronouns = profile?.Pronouns,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            BirthdayMonth = profile?.BirthdayMonth,
            BirthdayDay = profile?.BirthdayDay,
            Bio = profile?.Bio,
            ContributionInterests = profile?.ContributionInterests,
            BoardNotes = canViewLegalName ? profile?.BoardNotes : null,
            FirstName = canViewLegalName && profile is not null ? info.FirstName : null,
            LastName = canViewLegalName && profile is not null ? info.LastName : null,
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
            ContactFields = contactFields.OrderBy(cf => cf.DisplayOrder).Select(cf => new ContactFieldViewModel
            {
                Id = cf.Id,
                FieldType = cf.FieldType,
                Label = cf.Label,
                Value = cf.Value,
                Visibility = cf.Visibility
            }).ToList(),
            VolunteerHistory = (profile?.VolunteerHistory ?? [])
                .OrderByDescending(cv => cv.Date)
                .Select(cv => new VolunteerHistoryEntryViewModel
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
                    LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                    Proficiency = pl.Proficiency
                }).ToList(),
            PreferredLanguage = info.PreferredLanguage,
        };
        // The card offers one way to reach the member: the email it shows, or else the button.
        model.CanSendMessage = !isOwnProfile
            && model.PublicUserEmails.Count == 0
            && await commPrefService.AcceptsFacilitatedMessagesAsync(userId, ct);

        return View(Layouts.Contains(layout) ? layout : "Default", model);
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
