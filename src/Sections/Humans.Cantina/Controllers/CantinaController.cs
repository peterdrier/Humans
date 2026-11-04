using Humans.Cantina.Contracts;
using Humans.Base.Extensions;
using Humans.Cantina.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Cantina.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Cantina.Controllers;

/// <summary>
/// View-only. Members without the role get the cookie AccessDeniedPath redirect, not a bare 403.
/// Allergy and intolerance details (health data) render only for the one page load that follows an audited reveal
/// POST, handed over through TempData the way the IBAN reveal does; the aggregates and dietary columns always show.
/// </summary>
[Authorize(Policy = CantinaPolicies.Read)]
[Route("Cantina")]
internal sealed class CantinaController : HumansControllerBase
{
    /// <summary>TempData key on the reveal POST, ViewData key on the page that renders the details.</summary>
    internal const string HealthRevealedKey = "CantinaHealthRevealed";

    private readonly ICantinaRosterService _roster;
    private readonly ILogger<CantinaController> _logger;

    public CantinaController(
        ICantinaRosterService roster,
        ILogger<CantinaController> logger,
        IUserService userService) : base(userService)
    {
        _roster = roster;
        _logger = logger;
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(int? weekStartOffset = null, CancellationToken ct = default)
    {
        if (!ModelState.IsValid) return InvalidOffset(nameof(weekStartOffset));
        // No sort: the weekly page renders none of People; the CSV sorts for itself.
        var roster = await _roster.GetWeeklyRosterAsync(weekStartOffset, ct: ct).ConfigureAwait(false);
        ViewData[HealthRevealedKey] = TakeReveal(RevealToken("week", roster.WeekStartOffset));
        return View(roster);
    }

    /// <summary>Shows the week's free-text allergy and intolerance entries on the next render, audited.</summary>
    [HttpPost("Roster/RevealHealth")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealHealth(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Unauthorized();
        if (!ModelState.IsValid) return InvalidOffset(nameof(weekStartOffset));

        var roster = await _roster.GetWeeklyRosterAsync(weekStartOffset, RosterDisclosure.HealthReveal(actor.Id), ct).ConfigureAwait(false);
        TempData[HealthRevealedKey] = RevealToken("week", roster.WeekStartOffset);
        return RedirectToAction(nameof(Roster), new { weekStartOffset = roster.WeekStartOffset });
    }

    [HttpGet("Roster/Csv")]
    [Authorize(Policy = CantinaPolicies.Manage)]
    public async Task<IActionResult> Csv(int? weekStartOffset = null, CancellationToken ct = default)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Unauthorized();
        if (!ModelState.IsValid) return InvalidOffset(nameof(weekStartOffset));

        var roster = await _roster.GetWeeklyRosterAsync(weekStartOffset, RosterDisclosure.Export(actor.Id), ct).ConfigureAwait(false);
        roster = CantinaRosterAssembler.WithSortedPeople(roster);

        var bytes = CantinaRosterCsvWriter.Write(roster);
        var datePart = roster.WeekStartDate.ToInvariantDate() ?? "unknown";
        var filename = $"cantina-roster-week-of-{datePart}.csv";
        _logger.LogDebug(
            "Cantina roster CSV exported for weekStartOffset={WeekStartOffset}, people={PeopleCount}",
            roster.WeekStartOffset, roster.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }

    [HttpGet("Roster/Day")]
    public async Task<IActionResult> Day(int? dayOffset = null, CancellationToken ct = default)
    {
        if (!ModelState.IsValid) return InvalidOffset(nameof(dayOffset));
        var matrix = await _roster.GetDailyRosterAsync(dayOffset, ct: ct).ConfigureAwait(false);
        ViewData[HealthRevealedKey] = TakeReveal(RevealToken("day", matrix.DayOffset));
        return View(CantinaRosterAssembler.WithSortedPeople(matrix));
    }

    /// <summary>Shows the day matrix's allergy and intolerance columns on the next render, audited.</summary>
    [HttpPost("Roster/Day/RevealHealth")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealDayHealth(int? dayOffset = null, CancellationToken ct = default)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Unauthorized();
        if (!ModelState.IsValid) return InvalidOffset(nameof(dayOffset));

        var matrix = await _roster.GetDailyRosterAsync(dayOffset, RosterDisclosure.HealthReveal(actor.Id), ct).ConfigureAwait(false);
        TempData[HealthRevealedKey] = RevealToken("day", matrix.DayOffset);
        return RedirectToAction(nameof(Day), new { dayOffset = matrix.DayOffset });
    }

    [HttpGet("Roster/Day/Csv")]
    [Authorize(Policy = CantinaPolicies.Manage)]
    public async Task<IActionResult> DayCsv(int? dayOffset = null, CancellationToken ct = default)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Unauthorized();
        if (!ModelState.IsValid) return InvalidOffset(nameof(dayOffset));

        var matrix = await _roster.GetDailyRosterAsync(dayOffset, RosterDisclosure.Export(actor.Id), ct).ConfigureAwait(false);
        matrix = CantinaRosterAssembler.WithSortedPeople(matrix);

        var bytes = CantinaDailyMatrixCsvWriter.Write(matrix);
        var datePart = matrix.CalendarDate.ToInvariantDate() ?? "unknown";
        var filename = $"cantina-day-{datePart}-matrix.csv";
        _logger.LogDebug(
            "Cantina day matrix CSV exported for dayOffset={DayOffset}, people={PeopleCount}",
            matrix.DayOffset, matrix.People.Count);
        return File(bytes, "text/csv; charset=utf-8", filename);
    }

    private BadRequestObjectResult InvalidOffset(string parameter)
    {
        _logger.LogWarning("Rejected malformed Cantina roster offset {Parameter}", parameter);
        return BadRequest(ModelState);
    }

    // The token names the page the reveal was for, so a reveal never unlocks a different week or day.
    private static string RevealToken(string scope, int offset) => FormattableString.Invariant($"{scope}:{offset}");

    private bool TakeReveal(string expectedToken) =>
        TempData.TryGetValue(HealthRevealedKey, out var value) && value is string token
        && string.Equals(token, expectedToken, StringComparison.Ordinal);
}
