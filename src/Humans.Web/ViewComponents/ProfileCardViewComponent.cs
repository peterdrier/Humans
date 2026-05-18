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

public class ProfileCardViewComponent : ViewComponent
{
    private readonly IUserService _userService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly ICommunicationPreferenceService _commPrefService;

    public ProfileCardViewComponent(
        IUserService userService,
        IContactFieldService contactFieldService,
        IUserEmailService userEmailService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IMembershipCalculator membershipCalculator,
        ICommunicationPreferenceService commPrefService)
    {
        _userService = userService;
        _contactFieldService = contactFieldService;
        _userEmailService = userEmailService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _membershipCalculator = membershipCalculator;
        _commPrefService = commPrefService;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        var info = await _userService.GetUserInfoAsync(userId);
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
            canViewLegalName = await _roleAssignmentService.IsUserBoardMemberAsync(viewerId);
        }

        var contactFields = profile is not null
            ? await _contactFieldService.GetVisibleContactFieldsAsync(userId, viewerUserId)
            : [];

        // Get visible user emails based on access level
        ContactFieldVisibility accessLevel;
        if (isOwnProfile || viewMode == ProfileCardViewMode.Admin)
        {
            accessLevel = ContactFieldVisibility.BoardOnly;
        }
        else
        {
            accessLevel = await _contactFieldService.GetViewerAccessLevelAsync(userId, viewerUserId);
        }
        var visibleEmails = await _userEmailService.GetVisibleEmailsAsync(userId, accessLevel);

        var userTeams = await _teamService.GetUserTeamsAsync(userId);
        var displayableTeams = userTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers
                && (viewMode != ProfileCardViewMode.Public || !tm.Team.IsHidden))
            .OrderBy(tm => tm.Team.Name, StringComparer.Ordinal)
            .Select(tm => new TeamMembershipViewModel
            {
                TeamId = tm.TeamId,
                TeamName = tm.Team.DisplayName,
                TeamSlug = tm.Team.Slug,
                IsCoordinator = tm.Role == TeamMemberRole.Coordinator,
                IsSystemTeam = tm.Team.IsSystemTeam
            })
            .ToList();

        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId);

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
                && await _commPrefService.AcceptsFacilitatedMessagesAsync(userId)
        };

        return View(model);
    }
}
