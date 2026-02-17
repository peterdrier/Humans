using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Human")]
public class HumanController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;
    private readonly VolunteerHistoryService _volunteerHistoryService;
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;

    public HumanController(
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

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == id);

        if (profile == null || profile.IsSuspended)
        {
            return NotFound();
        }

        var viewer = await _userManager.GetUserAsync(User);
        if (viewer == null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(viewer.Id);
        var canViewLegalName = isOwnProfile || isBoardMember;

        var contactFields = await _contactFieldService.GetVisibleContactFieldsAsync(profile.Id, viewer.Id);

        var accessLevel = await _contactFieldService.GetViewerAccessLevelAsync(id, viewer.Id);
        var visibleEmails = await _userEmailService.GetVisibleEmailsAsync(id, accessLevel);

        var volunteerHistory = await _volunteerHistoryService.GetAllAsync(profile.Id);

        var userTeams = await _teamService.GetUserTeamsAsync(id);
        var displayableTeams = userTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .OrderBy(tm => tm.Team.Name, StringComparer.Ordinal)
            .Select(tm => new TeamMembershipViewModel
            {
                TeamId = tm.TeamId,
                TeamName = tm.Team.Name,
                TeamSlug = tm.Team.Slug,
                IsLead = tm.Role == TeamMemberRole.Lead,
                IsSystemTeam = tm.Team.IsSystemTeam
            })
            .ToList();

        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(id);
        var hasCustomPicture = profile.HasCustomProfilePicture;

        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            Email = profile.User.Email ?? string.Empty,
            DisplayName = profile.User.DisplayName,
            ProfilePictureUrl = profile.User.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture
                ? Url.Action("Picture", "Profile", new { id = profile.Id })
                : null,
            BurnerName = profile.BurnerName,
            FirstName = profile.FirstName,
            LastName = profile.LastName,
            City = profile.City,
            CountryCode = profile.CountryCode,
            Bio = profile.Bio,
            Pronouns = profile.Pronouns,
            ContributionInterests = profile.ContributionInterests,
            BoardNotes = canViewLegalName ? profile.BoardNotes : null,
            BirthdayMonth = profile.DateOfBirth?.Month,
            BirthdayDay = profile.DateOfBirth?.Day,
            EmergencyContactName = canViewLegalName ? profile.EmergencyContactName : null,
            EmergencyContactPhone = canViewLegalName ? profile.EmergencyContactPhone : null,
            EmergencyContactRelationship = canViewLegalName ? profile.EmergencyContactRelationship : null,
            HasPendingConsents = membershipSnapshot.PendingConsentCount > 0,
            PendingConsentCount = membershipSnapshot.PendingConsentCount,
            MembershipStatus = membershipSnapshot.Status.ToString(),
            IsApproved = profile.IsApproved,
            IsOwnProfile = isOwnProfile,
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
            Teams = displayableTeams
        };

        return View("~/Views/Profile/Index.cshtml", viewModel);
    }
}
