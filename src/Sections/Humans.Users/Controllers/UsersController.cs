using Humans.Base.Configuration;
using Humans.Base.Controllers;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Teams.Contracts;
using Humans.Users.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NodaTime;

namespace Humans.Users.Controllers;

[Authorize]
[Route("Users")]
internal sealed class UsersController(
    IUserService userService,
    ITeamService teamService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    IClock clock) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("Birthdays")]
    public async Task<IActionResult> Birthdays(int? month, CancellationToken ct)
    {
        var thisMonth = clock.GetCurrentInstant().InZone(HttpContext.Session.GetUserTimeZone()).Month;
        var currentMonth = month is >= 1 and <= 12 ? month.Value : thisMonth;

        var people = (await _userService.GetAllUserInfosAsync(ct))
            .Where(u => !u.IsSuspended
                        && u.Profile is { BirthdayDay: not null }
                        && u.Profile.BirthdayMonth == currentMonth)
            .OrderBy(u => u.Profile!.BirthdayDay)
            .ToList();

        var ids = people.Select(u => u.Id).ToHashSet();
        var teamsByUser = new Dictionary<Guid, List<string>>();
        foreach (var team in (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
        {
            foreach (var member in team.Members.Where(m => ids.Contains(m.UserId)))
            {
                if (!teamsByUser.TryGetValue(member.UserId, out var names))
                {
                    names = [];
                    teamsByUser[member.UserId] = names;
                }
                names.Add(team.Name);
            }
        }

        return View(new BirthdayCalendarViewModel
        {
            CurrentMonth = currentMonth,
            CurrentMonthName = new DateTime(2000, currentMonth, 1).ToMonthName(),
            Birthdays = people.Select(u => new BirthdayEntryViewModel
            {
                UserId = u.Id,
                DayOfMonth = u.Profile!.BirthdayDay!.Value,
                TeamNames = teamsByUser.GetValueOrDefault(u.Id, [])
            }).ToList()
        });
    }

    [HttpGet("Map")]
    public async Task<IActionResult> Map(CancellationToken ct)
    {
        var markers = (await _userService.GetAllUserInfosAsync(ct))
            .Where(u => !u.IsSuspended && u.Profile is { Latitude: not null, Longitude: not null })
            .Select(u => new MapMarkerViewModel
            {
                UserId = u.Id,
                DisplayName = u.BurnerName,
                ProfilePictureUrl = u.ProfilePictureUrl,
                Latitude = u.Profile!.Latitude!.Value,
                Longitude = u.Profile.Longitude!.Value,
                City = u.Profile.City,
                CountryCode = u.Profile.CountryCode
            })
            .ToList();

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(
            configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);

        return View(new MapViewModel { Markers = markers });
    }
}
