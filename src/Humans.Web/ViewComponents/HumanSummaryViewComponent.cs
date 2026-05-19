using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
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
public sealed class HumanSummaryViewComponent(IUserService userService, ITeamService teamService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var info = await userService.GetUserInfoAsync(userId);
        if (info is null)
        {
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                DisplayName = "(unknown)",
                HasProfile = false,
            });
        }

        if (info.Profile is null)
        {
            return View("Default", new ProfileSummaryViewModel
            {
                UserId = userId,
                DisplayName = info.BurnerName,
                Email = info.PrimaryEmail
                    ?? info.UserEmails.FirstOrDefault(e => e.IsVerified)?.Email,
                ProfilePictureUrl = info.ProfilePictureUrl,
                PreferredLanguage = info.PreferredLanguage,
                HasProfile = false,
            });
        }

        var profile = info.Profile;
        var memberships = (await teamService.GetActiveTeamMembershipsForUserAsync(userId))
            .OrderBy(m => m.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var publicTeams = memberships.Where(m => !m.IsHidden).Select(m => m.TeamName).ToList();
        var hiddenTeams = memberships.Where(m => m.IsHidden).Select(m => m.TeamName).ToList();

        var effectivePictureUrl = profile.HasCustomPicture
            ? Url.Action(nameof(ProfileController.Picture), "Profile",
                new { id = profile.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
            : info.ProfilePictureUrl;

        var isSuspended = profile.State == ProfileState.Suspended;

        var vm = new ProfileSummaryViewModel
        {
            UserId = userId,
            DisplayName = info.BurnerName,
            Email = info.Email,
            ProfilePictureUrl = effectivePictureUrl,
            PreferredLanguage = info.PreferredLanguage,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = isSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = isSuspended,
            Teams = publicTeams,
            HiddenTeams = hiddenTeams,
            Languages = profile.Languages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
        };

        return View("Default", vm);
    }
}
