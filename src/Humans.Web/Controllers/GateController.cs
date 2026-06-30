using Hangfire;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Infrastructure.Jobs;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
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
/// (Index/Evaluate/Leaderboard); the write actions (Decision, Claim/ClaimPin POST)
/// require the dedicated <see cref="PolicyNames.GateAdmit"/> policy so they never
/// ride on the read-only Scanner gate. A staffer "claims" the session with their
/// personal PIN so each scan is attributed to a real Human — the attribution id is
/// read from the session, never the request body. Supervisor overrides (too-early,
/// child-without-ID) carry the authorizing supervisor's PIN identity, brute-force
/// throttled on both the target user-id and the source device via
/// <see cref="GatePinThrottle"/>.
/// </summary>
[Authorize(Policy = PolicyNames.ScannerAccess)]
[Route("Gate")]
public sealed class GateController(
    IGateService gate,
    IUserServiceRead users,
    IConfiguration configuration,
    GatePinThrottle pinThrottle,
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
        var supervisors = await BuildSupervisorOptionsAsync(ct);
        var asOf = InstantPattern.ExtendedIso.Format(clock.GetCurrentInstant());
        return View(new GateIndexViewModel(
            info?.BurnerName ?? "Gate staff", DataStale: false, asOf, settings.CutoffConfigured, supervisors));
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
        string barcode, bool idConfirmed, bool childWithAdult, bool overrideEarly,
        Guid? supervisorUserId, string? supervisorPin, string? laneId, CancellationToken ct)
    {
        if (GetActiveScannerId() is not { } scanner)
            return Unauthorized();

        // Both the too-early override and the child-without-ID waiver require a server-verified
        // supervisor PIN (an authorizing identity recorded on the event). Verify before recording.
        Guid? overrideBy = null;
        if (childWithAdult || overrideEarly)
        {
            var (errorCard, authorized) = await AuthorizeSupervisorAsync(
                supervisorUserId, supervisorPin, barcode, childWithAdult, ct);
            if (errorCard is not null)
                return PartialView("_VerdictCard", errorCard);
            overrideBy = authorized;
        }

        var decision = await gate.RecordDecisionAsync(
            new GateDecisionInput(barcode, idConfirmed, childWithAdult, laneId,
                ClientScanAt: null, Note: null, OverrideByUserId: overrideBy),
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

    [HttpPost("Claim")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(Guid userId, CancellationToken ct)
    {
        // The claimant must be a real active member — the id comes from the form, so an
        // arbitrary/inactive guid must never reach PIN enrolment (attribution integrity).
        var info = await UserService.GetUserInfoAsync(userId, ct);
        if (info is not { IsActive: true })
            return RedirectToAction(nameof(Claim));

        // Don't stamp the session yet — resolve the staffer's PIN status and hand off to the
        // keypad (set a new PIN, verify the existing one, or block an un-enrolled supervisor).
        var status = await gate.GetPinStatusAsync(userId, ct);
        return View("Pin", GatePinViewModel.ForClaim(userId, info.BurnerName, status));
    }

    [HttpPost("ClaimPin")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClaimPin(Guid userId, string? pin, CancellationToken ct)
    {
        // The claimant must be a real active member — guard before any enrol/verify/stamp, so a
        // direct POST with an arbitrary or inactive id can't mint a PIN or claim the session.
        var info = await UserService.GetUserInfoAsync(userId, ct);
        if (info is not { IsActive: true })
            return RedirectToAction(nameof(Claim));

        // Re-derive the mode from the server (never trust a client-supplied set/verify hint).
        var status = await gate.GetPinStatusAsync(userId, ct);
        var name = info.BurnerName;

        // A supervisor with no PIN can't self-enrol at the anonymous kiosk — re-show the block.
        if (status is { HasPin: false, IsSupervisor: true })
            return View("Pin", GatePinViewModel.ForClaim(userId, name, status));

        return status.HasPin
            ? await VerifyClaimAsync(userId, name, status, pin, ct)
            : await SetClaimPinAsync(userId, name, status, pin, ct);
    }

    // Name-only people search for the claim screen. The route-locked kiosk can't reach
    // /api/profiles/search (that lock is what keeps the supervisor-override picker a tap-list),
    // so the claim picker points here instead. Burner names only — no email, no broad fields —
    // and it stays inside the /Gate route-lock.
    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length < 2)
            return Json(Array.Empty<object>());

        var matches = await UserService.SearchUsersAsync(query, PersonSearchFields.Name, 10, ct);
        var rows = matches
            .OrderByRelevance()
            .Select(m => new
            {
                userId = m.UserId,
                displayName = m.BurnerName,
                detail = (string?)null,
                profilePictureUrl = m.ProfilePictureUrl,
            })
            .ToList();
        return Json(rows);
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

    // Admin PIN enrolment: set any user's PIN (incl. supervisors, whose PINs carry override
    // authority and so are never self-enrolled at the kiosk).
    [HttpPost("Admin/SetPin")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> SetStaffPin(Guid userId, string? pin, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            SetError("Pick a person before setting a PIN.");
        else if (await gate.AdminSetPinAsync(userId, pin ?? string.Empty, ct))
            SetSuccess("PIN set.");
        else
            SetError("PIN must be 4 digits and not trivially guessable (no 1234, 0000, or repeats).");

        return RedirectToAction(nameof(Admin));
    }

    // Admin PIN reset: clear a user's PIN; they re-enrol on their next claim.
    [HttpPost("Admin/ResetPin")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> ResetStaffPin(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            SetError("Pick a person before resetting a PIN.");
        else
        {
            await gate.ClearPinAsync(userId, ct);
            SetSuccess("PIN reset — they'll set a new one on their next claim.");
        }

        return RedirectToAction(nameof(Admin));
    }

    // ── Claim helpers ────────────────────────────────────────────────────────
    private async Task<IActionResult> VerifyClaimAsync(
        Guid userId, string name, GatePinStatus status, string? pin, CancellationToken ct)
    {
        var userKey = UserKey(userId);
        if (ThrottleWait(userKey) is { } wait)
            return View("Pin", GatePinViewModel.ForClaim(userId, name, status, $"Too many tries — wait {wait}s"));

        if (!await gate.VerifyPinAsync(userId, pin ?? string.Empty, ct))
        {
            RecordPinFailure(userKey);
            return View("Pin", GatePinViewModel.ForClaim(userId, name, status, "That PIN didn't match — try again"));
        }

        ResetPinThrottle(userKey);
        return StampAndScan(userId);
    }

    private async Task<IActionResult> SetClaimPinAsync(
        Guid userId, string name, GatePinStatus status, string? pin, CancellationToken ct) =>
        await gate.SetOwnPinAsync(userId, pin ?? string.Empty, ct) switch
        {
            GatePinSetResult.Ok => StampAndScan(userId),
            GatePinSetResult.InvalidPin => View("Pin", GatePinViewModel.ForClaim(
                userId, name, status, "Pick a less obvious PIN — not 1234, 0000, or repeats")),
            // Role changed to supervisor between the GET and this POST — show the admin-enrol block.
            _ => View("Pin", new GatePinViewModel(userId, name, GatePinMode.BlockedSupervisor, null)),
        };

    private IActionResult StampAndScan(Guid userId)
    {
        // Stamp the verified user-id server-side (never from the request body) so attribution can't be forged.
        HttpContext.Session.SetString(ScannerSessionKey, userId.ToString());
        return RedirectToAction(nameof(Index));
    }

    // ── Override authorization ───────────────────────────────────────────────
    private async Task<(GateScanCardViewModel? ErrorCard, Guid? SupervisorUserId)> AuthorizeSupervisorAsync(
        Guid? supervisorUserId, string? pin, string barcode, bool childWithAdult, CancellationToken ct)
    {
        // No supervisor picked → nothing to authorize, and no per-person bucket to throttle.
        if (supervisorUserId is not { } supId)
            return (OverrideErrorCard(barcode, childWithAdult, "Pick the authorizing supervisor, then enter their PIN"), null);

        // Throttle per target supervisor only. A shared device/IP key would let one locked-out
        // attempt (or a fat-fingered PIN) freeze every claim and override at the terminal.
        var userKey = UserKey(supId);
        if (ThrottleWait(userKey) is { } wait)
            return (OverrideErrorCard(barcode, childWithAdult, $"Too many PIN attempts — wait {wait}s"), null);

        if (await gate.AuthorizeOverrideAsync(supId, pin ?? string.Empty, ct))
        {
            ResetPinThrottle(userKey);
            return (null, supId);
        }

        RecordPinFailure(userKey);
        return (OverrideErrorCard(barcode, childWithAdult, "Supervisor PIN not accepted — check the name & PIN"), null);
    }

    private static GateScanCardViewModel OverrideErrorCard(string barcode, bool childWithAdult, string reason) =>
        new(GateCardKind.Amber, "Supervisor", null, reason, IsEarly: false, barcode,
            AllowChildWithAdult: false,
            // A too-early override can retry inline (the barcode is preserved on the card). The child
            // waiver lives on the ID-confirm card, so a failed child auth just re-scans.
            AllowSupervisorOverride: !childWithAdult);

    // ── Throttle helpers (keyed on the target user-id only) ──────────────────────
    // Per-target-user, never per shared device/IP: a 4-digit PIN's brute-force ceiling is
    // already capped per user (5 / 15 min), and a shared-device key would let one bad run
    // lock out the whole terminal — the gate-wide-lockout DoS we deliberately avoid.
    private static string UserKey(Guid userId) => $"u:{userId}";

    private int? ThrottleWait(string userKey) => pinThrottle.SecondsUntilRetry(userKey);

    private void RecordPinFailure(string userKey) => pinThrottle.RecordFailure(userKey);

    private void ResetPinThrottle(string userKey) => pinThrottle.Reset(userKey);

    // ── Lookups ──────────────────────────────────────────────────────────────
    private async Task<IReadOnlyList<GateRosterMember>> BuildRosterAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(configuration["Gate:RosterTeamId"], out var teamId))
            return [];

        var roster = await gate.GetShiftRosterAsync(teamId, ct);
        return roster.Select(r => new GateRosterMember(r.UserId, r.DisplayName)).ToList();
    }

    private async Task<IReadOnlyList<GateSupervisorOption>> BuildSupervisorOptionsAsync(CancellationToken ct)
    {
        var ids = await gate.GetEnrolledSupervisorIdsAsync(ct);
        var options = new List<GateSupervisorOption>(ids.Count);
        foreach (var id in ids)
        {
            var info = await UserService.GetUserInfoAsync(id, ct);
            if (info is { IsActive: true })
                options.Add(new GateSupervisorOption(id, info.BurnerName));
        }

        return options.OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private Guid? GetActiveScannerId() =>
        Guid.TryParse(HttpContext.Session.GetString(ScannerSessionKey), out var id) ? id : null;
}
