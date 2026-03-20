using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Controllers;
using Humans.Web.Models;

namespace Humans.Web.ViewComponents;

public enum ProfileCardViewMode
{
    Self,
    Public,
    Admin
}

public class ProfileCardViewComponent : ViewComponent
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;
    private readonly VolunteerHistoryService _volunteerHistoryService;
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;

    public ProfileCardViewComponent(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IContactFieldService contactFieldService,
        IUserEmailService userEmailService,
        VolunteerHistoryService volunteerHistoryService,
        ITeamService teamService,
        IMembershipCalculator membershipCalculator)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _contactFieldService = contactFieldService;
        _userEmailService = userEmailService;
        _volunteerHistoryService = volunteerHistoryService;
        _teamService = teamService;
        _membershipCalculator = membershipCalculator;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return Content(string.Empty);
        }

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        var isOwnProfile = viewMode == ProfileCardViewMode.Self;
        var canViewLegalName = viewMode != ProfileCardViewMode.Public;
        var viewerUserId = userId; // default for self/admin

        // For public view, resolve actual permissions based on viewer
        if (viewMode == ProfileCardViewMode.Public)
        {
            var viewer = await _userManager.GetUserAsync(UserClaimsPrincipal);
            if (viewer != null)
            {
                viewerUserId = viewer.Id;
                canViewLegalName = await _teamService.IsUserBoardMemberAsync(viewer.Id);
            }
        }

        // Get contact fields
        var contactFields = profile != null
            ? await _contactFieldService.GetVisibleContactFieldsAsync(profile.Id, viewerUserId)
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

        // Get volunteer history entries
        var volunteerHistory = profile != null
            ? await _volunteerHistoryService.GetAllAsync(profile.Id)
            : [];

        // Get user's teams (excluding Volunteers system team)
        var userTeams = await _teamService.GetUserTeamsAsync(userId);
        var displayableTeams = userTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
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

        // Get membership status
        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId);

        var hasCustomPicture = profile?.HasCustomProfilePicture == true;
        var pictureUrl = hasCustomPicture
            ? Url.Action(nameof(ProfileController.Picture), "Profile", new { id = profile!.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
            : null;

        var model = new ProfileCardViewModel
        {
            UserId = userId,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = pictureUrl,
            BurnerName = profile?.BurnerName ?? string.Empty,
            Pronouns = profile?.Pronouns,
            MembershipStatus = membershipSnapshot.Status.ToString(),
            IsApproved = profile?.IsApproved ?? false,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            BirthdayMonth = profile?.DateOfBirth?.Month,
            BirthdayDay = profile?.DateOfBirth?.Day,
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
                IsNotificationTarget = e.IsNotificationTarget,
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
            VolunteerHistory = volunteerHistory.Select(vh => new VolunteerHistoryEntryViewModel
            {
                Id = vh.Id,
                Date = vh.Date,
                EventName = vh.EventName,
                Description = vh.Description
            }).ToList(),
            Teams = displayableTeams,
            PreferredLanguage = user.PreferredLanguage,
            CanSendMessage = !isOwnProfile
                && !visibleEmails.Any(e => e.Visibility >= ContactFieldVisibility.AllActiveProfiles)
        };

        return View(model);
    }
}
