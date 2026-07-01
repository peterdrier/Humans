using Hangfire;
using Humans.Application;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Infrastructure;
using Humans.Web.Models;
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
/// (Index/Evaluate/Leaderboard); the write action (Decision) requires the dedicated
/// <see cref="PolicyNames.GateAdmit"/> policy so it never rides on the read-only
/// Scanner gate. Scans are attributed to the authenticated gate account, taken from
/// the principal (never the request body). Too-early and child-without-ID admits are
/// a plain operator override — a button tap, no PIN.
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
        // No claim step: the terminal scans as its authenticated gate account (never a per-staffer
        // PIN). The attribution id is taken from the principal — never the request body.
        if (GetCurrentUserId() is not { } scanner)
            return Unauthorized();

        var info = await UserService.GetUserInfoAsync(scanner, ct);
        var settings = await gate.GetSettingsAsync(ct);
        var asOf = InstantPattern.ExtendedIso.Format(clock.GetCurrentInstant());
        return View(new GateIndexViewModel(
            info?.BurnerName ?? "Gate staff", DataStale: false, asOf, settings.CutoffConfigured, []));
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
        string barcode, bool idConfirmed, bool childWithAdult, bool overrideEarly,
        string? laneId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } scanner)
            return Unauthorized();

        // Too-early and child-without-ID admits are a plain no-PIN override now: the operator's tap on
        // the override button IS the authorization. Record it against the gate account (no supervisor).
        Guid? overrideBy = childWithAdult || overrideEarly ? scanner : null;

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
        // keypad (set a new PIN, or verify the existing one).
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

        return status.HasPin
            ? await VerifyClaimAsync(userId, name, status, pin, ct)
            : await SetClaimPinAsync(userId, name, status, pin, ct);
    }

    [HttpPost("EndShift")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public IActionResult EndShift()
    {
        // End the current scanner's session so the terminal drops back to "Who is scanning?".
        // Clearing attribution server-side (not just navigating away) means a walk-away can't
        // leave the next person's scans recorded against whoever last claimed — the session
        // otherwise survives ~8h of idle, long past a single staffer's shift.
        HttpContext.Session.Remove(ScannerSessionKey);
        return RedirectToAction(nameof(Claim));
    }

    // Name-only people search for the claim screen. The route-locked kiosk can't reach
    // /api/profiles/search (that lock is what keeps the supervisor-override picker a tap-list),
    // so the claim picker points here instead. Matching is burner-name only — never by email or
    // broad fields — but each result shows a *masked* email as a disambiguator (see below), and it
    // stays inside the /Gate route-lock.
    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length < 2)
            return Json(Array.Empty<HumanLookupSearchResult>());

        // Name-only search (never email — see the note above). To tell same-name staffers apart
        // (many volunteers share a first name), each result carries a *masked* primary email as a
        // disambiguator — enough to recognise your own address, not enough to broadcast it. The
        // effective email is read per result (cached) and masked here at the presentation layer, so
        // the search itself and its response shape stay email-free. Mirrors BuildSupervisorOptionsAsync.
        var matches = (await UserService.SearchUsersAsync(query, PersonSearchFields.Name, 10, ct))
            .OrderByRelevance()
            .ToList();
        var rows = new List<HumanLookupSearchResult>(matches.Count);
        foreach (var m in matches)
        {
            var info = await UserService.GetUserInfoAsync(m.UserId, ct);
            rows.Add(new HumanLookupSearchResult(
                m.UserId, m.BurnerName, Detail: MaskEmail(PublicEmail(info)), ProfilePictureUrl: m.ProfilePictureUrl));
        }
        return Json(rows);
    }

    // The one email safe to hint on a shared, role-less kiosk: a verified address the owner set to
    // org-public (AllActiveProfiles) visibility — never a Board/coordinator/team-scoped address, and
    // never a merged/GDPR tombstone. Mirrors the Bio-bucket public-email rule; if the person has no
    // org-public email, the picker simply shows no disambiguator (the photo still helps).
    private static string? PublicEmail(UserInfo? info) =>
        info is null || info.IsTombstone
            ? null
            : info.UserEmails
                .Where(e => e.IsVerified && e.Visibility == ContactFieldVisibility.AllActiveProfiles)
                .OrderByDescending(e => e.IsPrimary)
                .Select(e => e.Email)
                .FirstOrDefault();

    // Masks an email for the shared kiosk picker: first char + bullets + last char of the local
    // part, then the full domain (paul.smith@gmail.com → p•••h@gmail.com). Recognisable to its
    // owner, minimal to a bystander. Presentation-only — never used for matching.
    private static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == email.Length - 1) return null;
        var local = email[..at];
        var domain = email[(at + 1)..];
        var shown = local.Length <= 2 ? $"{local[0]}•••" : $"{local[0]}•••{local[^1]}";
        return $"{shown}@{domain}";
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
        if (GetCurrentUserId() is not { } actor)
            return Unauthorized();
        if (userId == Guid.Empty)
            SetError("Pick a person before setting a PIN.");
        else if (await gate.AdminSetPinAsync(userId, pin ?? string.Empty, actor, ct))
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
        if (GetCurrentUserId() is not { } actor)
            return Unauthorized();
        if (userId == Guid.Empty)
            SetError("Pick a person before resetting a PIN.");
        else
        {
            await gate.ClearPinAsync(userId, actor, ct);
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
            // InvalidPin (or any non-Ok) → re-show the keypad with the "pick a better PIN" hint.
            _ => View("Pin", GatePinViewModel.ForClaim(
                userId, name, status, "Pick a less obvious PIN — not 1234, 0000, or repeats")),
        };

    private IActionResult StampAndScan(Guid userId)
    {
        // Stamp the verified user-id server-side (never from the request body) so attribution can't be forged.
        HttpContext.Session.SetString(ScannerSessionKey, userId.ToString());
        return RedirectToAction(nameof(Index));
    }

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

}
