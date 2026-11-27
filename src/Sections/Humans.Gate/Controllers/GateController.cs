using Humans.Tickets.Contracts;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Gate.Controllers;

/// <summary>
/// Reads need <see cref="PolicyNames.ScannerAccess"/>; the Decision write needs <see cref="PolicyNames.GateAdmit"/>, so
/// admission never rides on the read-only Scanner gate. Scans are attributed from the principal, never the request.
/// </summary>
[Authorize(Policy = PolicyNames.ScannerAccess)]
[Route("Gate")]
internal sealed class GateController(
    IGateService gate,
    IUserService users,
    IClock clock,
    ILogger<GateController> logger) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var settings = await gate.GetSettingsAsync(ct);
        var asOf = InstantPattern.ExtendedIso.Format(clock.GetCurrentInstant());
        return View(new GateIndexViewModel(asOf, settings.CutoffConfigured));
    }

    [HttpGet("Evaluate")]
    public async Task<IActionResult> Evaluate(string barcode, CancellationToken ct)
    {
        var result = await gate.EvaluateAsync(barcode, ct);
        return PartialView("_VerdictCard", GateScanCardViewModel.FromEvaluation(result));
    }

    [HttpPost("Decision")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decision(
        string barcode, bool? idConfirmed, bool childWithAdult, bool overrideEarly,
        string? supervisorPin, string? laneId, CancellationToken ct)
    {
        var scanner = await GetCurrentUserInfoAsync(ct);
        if (scanner is null)
            return Unauthorized();

        if (!ModelState.IsValid || idConfirmed is not { } confirmed)
        {
            var problem = ModelState.IsValid
                ? "no idConfirmed value"
                : "malformed " + string.Join(", ", ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Key));
            logger.LogWarning("Rejected gate decision from scanner {ScannerId}: {Problem}", scanner.Id, problem);
            return BadRequest();
        }

        var decision = await gate.RecordDecisionAsync(
            new GateDecisionInput(barcode, confirmed, childWithAdult, laneId,
                ClientScanAt: null, Note: null, OverrideEarly: overrideEarly, SupervisorPin: supervisorPin),
            scanner.Id, ct);

        if (decision.InputTooLong) return BadRequest();

        return PartialView("_VerdictCard", GateScanCardViewModel.FromDecision(decision, barcode, childWithAdult));
    }

    [HttpGet("Find")]
    public async Task<IActionResult> Find(string? q, CancellationToken ct)
    {
        var matches = (await gate.FindTicketsByNameAsync(q, ct))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return PartialView("_FindResults", new GateFindResultsViewModel(q?.Trim() ?? string.Empty, matches));
    }

    [HttpPost("Undo")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Undo(string barcode, string? supervisorPin, CancellationToken ct)
    {
        var scanner = await GetCurrentUserInfoAsync(ct);
        if (scanner is null)
            return Unauthorized();

        var result = await gate.UndoAdmitAtGateAsync(barcode, supervisorPin, scanner.Id, ct);
        return PartialView("_VerdictCard", GateScanCardViewModel.FromUndo(result, barcode));
    }

    // The save target of the /Settings#gate tab.
    [HttpPost("Admin")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TicketsPolicies.Manage)]
    public async Task<IActionResult> Admin(GateSettingsViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError("Invalid gate settings.");
            return Redirect("/Settings#gate");
        }

        var parsed = InstantPattern.ExtendedIso.Parse(model.GeneralEntryOpensAtUtc ?? string.Empty);
        if (!parsed.Success)
        {
            SetError("Invalid date/time — use an ISO instant, e.g. 2026-07-06T10:00:00Z.");
            return Redirect("/Settings#gate");
        }

        await gate.SaveSettingsAsync(new GateSettingsDto(parsed.Value), ct);
        SetSuccess("Gate settings saved.");
        return Redirect("/Settings#gate");
    }
}
