using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Web.Controllers;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders the same content as the hover popover (<c>_HumanPopover.cshtml</c>)
/// inline — avatar, name, membership badge, city/country, public teams +
/// (admin-only) hidden teams, and languages. Use on admin-review screens
/// (e.g. ticket-transfer detail) where the reviewer needs identity context
/// without having to hover.
/// </summary>
public sealed class HumanSummaryViewComponent : ViewComponent
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;

    public HumanSummaryViewComponent(
        IUserService userService,
        IProfileService profileService,
        IUserEmailService userEmailService,
        ITeamService teamService)
    {
        _userService = userService;
        _profileService = profileService;
        _userEmailService = userEmailService;
        _teamService = teamService;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var user = await _userService.GetByIdAsync(userId);
        if (user is null)
        {
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                DisplayName = "(unknown)",
                HasProfile = false,
            });
        }

        var profile = await _profileService.GetProfileAsync(userId);
        if (profile is null)
        {
            var userEmails = await _userEmailService.GetUserEmailsAsync(userId);
            var fallbackEmail = userEmails.FirstOrDefault(e => e.IsVerified && e.IsPrimary)?.Email
                ?? userEmails.FirstOrDefault(e => e.IsVerified)?.Email;
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                DisplayName = user.DisplayName,
                Email = fallbackEmail,
                ProfilePictureUrl = user.ProfilePictureUrl,
                PreferredLanguage = user.PreferredLanguage,
                HasProfile = false,
            });
        }

        var memberships = (await _teamService.GetActiveTeamMembershipsForUserAsync(userId))
            .OrderBy(m => m.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var publicTeams = memberships.Where(m => !m.IsHidden).Select(m => m.TeamName).ToList();
        var hiddenTeams = memberships.Where(m => m.IsHidden).Select(m => m.TeamName).ToList();
        var profileLanguages = await _profileService.GetProfileLanguagesAsync(profile.Id);

        var effectivePictureUrl = profile.HasCustomProfilePicture
            ? Url.Action(nameof(ProfileController.Picture), "Profile",
                new { id = profile.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
            : user.ProfilePictureUrl;

        var vm = new ProfileSummaryViewModel
        {
            UserId = userId,
            DisplayName = !string.IsNullOrWhiteSpace(profile.BurnerName)
                ? profile.BurnerName
                : user.DisplayName,
            Email = user.Email,
            ProfilePictureUrl = effectivePictureUrl,
            PreferredLanguage = user.PreferredLanguage,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = profile.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = profile.IsSuspended,
            Teams = publicTeams,
            HiddenTeams = hiddenTeams,
            Languages = profileLanguages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
        };

        return View("Default", vm);
    }
}
