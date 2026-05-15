using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Contacts")]
public sealed class TicketsContactsAdminController : HumansControllerBase
{
    private readonly IAttendeeContactImportService _import;
    private readonly ILogger<TicketsContactsAdminController> _logger;

    public TicketsContactsAdminController(
        IAttendeeContactImportService import,
        UserManager<User> userManager,
        ILogger<TicketsContactsAdminController> logger)
        : base(userManager)
    {
        _import = import;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var plan = await _import.BuildPlanAsync(ct);
        var rows = plan.Decisions
            .OrderBy(d => SortKey(d.Outcome))
            .ThenBy(d => d.Email, StringComparer.OrdinalIgnoreCase)
            .Select(d => new AttendeeImportDecisionRow(
                d.AttendeeId, d.Email, d.AttendeeName, d.VendorTicketId,
                d.Outcome, d.TargetUserId, d.UnverifiedRowUserId, d.AmbiguousUserIds))
            .ToList();

        return View("~/Views/Tickets/Admin/Contacts.cshtml",
            new ContactImportPreviewViewModel(plan, rows));
    }

    [HttpPost("Apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(
        [FromForm(Name = "selected")] Guid[] selected,
        CancellationToken ct)
    {
        if (selected is null || selected.Length == 0)
        {
            SetError("Select at least one attendee before applying.");
            return RedirectToAction(nameof(Index));
        }

        var actor = await GetCurrentUserAsync();
        if (actor is null)
        {
            SetError("Could not resolve current user.");
            return RedirectToAction(nameof(Index));
        }

        var fresh = await _import.BuildPlanAsync(ct);
        var result = await _import.ApplyAsync(fresh, new HashSet<Guid>(selected), actor.Id, ct);
        SetInfo($"Attendee contact import: {result.FormatSummary()}");
        return RedirectToAction(nameof(Index));
    }

    private static int SortKey(AttendeeImportOutcome o) => o switch
    {
        AttendeeImportOutcome.AmbiguousMultipleVerified => 0,
        AttendeeImportOutcome.DeleteUnverifiedThenCreate => 1,
        AttendeeImportOutcome.CreateNewUser => 2,
        AttendeeImportOutcome.AttachVerified => 3,
        AttendeeImportOutcome.SkipNoEmail => 4,
        AttendeeImportOutcome.SkipVoided => 5,
        _ => 99,
    };
}
