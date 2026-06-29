using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Humans.Web.Authorization;
using Humans.Web.Infrastructure;
using Humans.Web.Models.Gate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>
/// Gate admissions terminal. Distinct from the read-only <c>Scanner</c> section:
/// this one decides entry and writes the durable <c>gate_scan_events</c> record.
/// The terminal authenticates via the <see cref="PolicyNames.ScannerAccess"/>
/// policy (shared gate-terminal account or a ticket admin) for the read actions
/// (Index/Evaluate/Leaderboard); the write actions (Decision, Claim POST) require
/// the dedicated <see cref="PolicyNames.GateAdmit"/> policy so they never ride on
/// the read-only Scanner gate. The individual staffer "claims" the session so each
/// scan is attributed to a real Human — the attribution id is read from the
/// session, never the request body.
/// </summary>
[Authorize(Policy = PolicyNames.ScannerAccess)]
[Route("Gate")]
public sealed class GateController(
    IGateService gate,
    IUserServiceRead users,
    IShiftManagementService shifts,
    IConfiguration configuration,
    GateLoginThrottle pinThrottle,
    IClock clock) : HumansControllerBase(users)
{
    private const string ScannerSessionKey = "GateScannerId";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (GetActiveScannerId() is not { } scanner)
            return RedirectToAction(nameof(Claim));

        var info = await UserService.GetUserInfoAsync(scanner, ct);
        var settings = await gate.GetSettingsAsync(ct);
        var asOf = InstantPattern.ExtendedIso.Format(clock.GetCurrentInstant());
        return View(new GateIndexViewModel(
            info?.BurnerName ?? "Gate staff", DataStale: false, asOf, settings.CutoffConfigured));
    }

    [HttpGet("Evaluate")]
    public async Task<IActionResult> Evaluate(string barcode, CancellationToken ct)
    {
        if (GetActiveScannerId() is null)
            return Unauthorized();

        var result = await gate.EvaluateAsync(barcode, ct);
        return PartialView("_VerdictCard", GateScanCardViewModel.FromEvaluation(result));
    }

    [HttpPost("Decision")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decision(
        string barcode, bool idConfirmed, bool childWithAdult,
        string? supervisorPin, string? laneId, CancellationToken ct)
    {
        if (GetActiveScannerId() is not { } scanner)
            return Unauthorized();

        // The ID waiver (child with adult) needs a server-verified supervisor PIN.
        // Throttle PIN attempts per source IP — a static PIN on a write endpoint is
        // otherwise brute-forceable (mirrors the /Account/GateLogin throttle, on its
        // own bucket so the two surfaces don't share a failure count).
        if (childWithAdult)
        {
            var pinSource = $"GatePin:{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

            if (pinThrottle.SecondsUntilRetry(pinSource) is { } waitSeconds)
                return PartialView("_VerdictCard", new GateScanCardViewModel(
                    GateCardKind.Amber, "Supervisor", null,
                    $"Too many PIN attempts — wait {waitSeconds}s", false, barcode, false));

            if (!SupervisorPinValid(supervisorPin))
            {
                pinThrottle.RecordFailure(pinSource);
                return PartialView("_VerdictCard", new GateScanCardViewModel(
                    GateCardKind.Amber, "Supervisor", null,
                    "Supervisor PIN required to admit a child without ID", false, barcode, false));
            }

            pinThrottle.Reset(pinSource);
        }

        var decision = await gate.RecordDecisionAsync(
            new GateDecisionInput(barcode, idConfirmed, childWithAdult, laneId, ClientScanAt: null, Note: null),
            scanner, ct);

        // Best-effort vendor check-in mirror on admit — fire-and-forget so the gate never waits.
        if (decision.VendorTicketId is { Length: > 0 } vendorTicketId)
            BackgroundJob.Enqueue<GateVendorCheckInJob>(j => j.ExecuteAsync(vendorTicketId, CancellationToken.None));

        return PartialView("_VerdictCard", GateScanCardViewModel.FromDecision(decision, barcode));
    }

    [HttpGet("Claim")]
    public async Task<IActionResult> Claim(CancellationToken ct)
    {
        // Quick-pick the gate-shift roster so a staffer taps their name at shift start;
        // the search box below it still finds anyone (helpers who aren't rostered). The
        // roster is opt-in: empty unless an admin points Gate:RosterTeamId at the Shifts
        // department that staffs the gate, so unconfigured deployments just get search.
        var roster = await BuildRosterAsync(ct);
        return View(new GateClaimViewModel(roster));
    }

    private async Task<IReadOnlyList<GateRosterMember>> BuildRosterAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(configuration["Gate:RosterTeamId"], out var teamId))
            return [];

        var activeEvent = await shifts.GetActiveAsync();
        if (activeEvent is null)
            return [];

        var gateShifts = await shifts.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            activeEvent.Id, DepartmentId: teamId, Flags: ShiftBrowseQueryFlags.IncludeSignups));

        // "Signed up" = not a negative terminal state (Refused/Bailed/Cancelled/NoShow);
        // one person can hold several gate shifts, so de-dupe to one pick per human.
        return gateShifts
            .SelectMany(s => s.Signups)
            .Where(u => u.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .DistinctBy(u => u.UserId)
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new GateRosterMember(u.UserId, u.DisplayName))
            .ToList();
    }

    [HttpPost("Claim")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public IActionResult Claim(Guid userId)
    {
        HttpContext.Session.SetString(ScannerSessionKey, userId.ToString());
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Leaderboard")]
    public async Task<IActionResult> Leaderboard(CancellationToken ct)
    {
        var since = clock.GetCurrentInstant().Minus(Duration.FromDays(7));
        var board = await gate.GetLeaderboardAsync(since, ct);

        var rows = board.Rows
            .OrderByDescending(r => r.Admitted)
            .ThenByDescending(r => r.Total)
            .Select(r => new GateLeaderboardRowViewModel(r.ScannedByUserId, r.Admitted, r.Rejected, r.Total))
            .ToList();

        ViewData["TotalAdmitted"] = board.TotalAdmitted;
        ViewData["TotalScanned"] = board.TotalScanned;
        return View(rows);
    }

    [HttpGet("Admin")]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> Admin(CancellationToken ct)
    {
        var s = await gate.GetSettingsAsync(ct);
        return View(new GateSettingsViewModel(
            InstantPattern.ExtendedIso.Format(s.GeneralEntryOpensAt), s.MinorAgeThresholdYears));
    }

    [HttpPost("Admin")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> Admin(GateSettingsViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(model);

        var parsed = InstantPattern.ExtendedIso.Parse(model.GeneralEntryOpensAtUtc ?? string.Empty);
        if (!parsed.Success)
        {
            SetError("Invalid date/time — use an ISO instant, e.g. 2026-07-06T10:00:00Z.");
            return View(model);
        }

        await gate.SaveSettingsAsync(new GateSettingsDto(parsed.Value, model.MinorAgeThresholdYears), ct);
        SetSuccess("Gate settings saved.");
        return RedirectToAction(nameof(Admin));
    }

    private Guid? GetActiveScannerId() =>
        Guid.TryParse(HttpContext.Session.GetString(ScannerSessionKey), out var id) ? id : null;

    private bool SupervisorPinValid(string? pin)
    {
        var configured = configuration["Gate:SupervisorPin"];
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(pin))
            return false;

        // Compare fixed-width hashes so neither equality nor PIN length leaks via timing.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(pin));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
