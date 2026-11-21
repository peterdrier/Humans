using Humans.Base.Controllers;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

namespace Humans.Gate.Controllers;

/// <summary>
/// Arrivals for the ticket team: Read sees the report, only Manage undoes a check-in. The gate-terminal account holds
/// neither, so the kiosk undoes through the supervisor PIN on <see cref="GateController.Undo"/> instead.
/// </summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Gate/Report")]
internal sealed class GateReportController(
    IUserService users,
    IGateService gate,
    IAuthorizationService authorization) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? day, CancellationToken ct)
    {
        var report = await gate.GetArrivalsReportAsync(ct);
        if (report is null)
            return View(model: null);

        var shown = LocalDatePattern.Iso.Parse(day ?? string.Empty) is { Success: true } parsed
            ? parsed.Value
            : report.Days.Any(d => d.Day == report.Today) || report.Days.Count == 0
                ? report.Today
                : report.Days.Max(d => d.Day);

        var canUndo = (await authorization.AuthorizeAsync(User, TicketsPolicies.Manage)).Succeeded;
        return View(new GateReportViewModel(
            report,
            shown,
            report.Days.OrderByDescending(d => d.Day).ToList(),
            report.Arrivals.Where(a => a.Day == shown).OrderByDescending(a => a.AdmittedAt).ToList(),
            canUndo));
    }

    [HttpPost("Undo")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Undo(Guid scanId, string? reason, string? day, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Unauthorized();

        var result = await gate.UndoAdmitAsync(scanId, reason, actor.Id, ct);
        switch (result.Outcome)
        {
            case GateUndoOutcome.Undone:
                SetSuccess("Check-in undone. The ticket can be scanned again.");
                break;
            case GateUndoOutcome.ReasonRequired:
                SetError("Say why the check-in is undone (up to 1000 characters).");
                break;
            case GateUndoOutcome.VendorCheckedIn:
                SetError("Ticket Tailor has this check-in too and has no undo, so the next ticket sync would bring it back. Undo it in Ticket Tailor first.");
                break;
            default:
                SetError("That check-in was already undone.");
                break;
        }

        return RedirectToAction(nameof(Index), new { day });
    }
}
