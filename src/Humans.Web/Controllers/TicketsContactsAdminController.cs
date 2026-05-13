using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
