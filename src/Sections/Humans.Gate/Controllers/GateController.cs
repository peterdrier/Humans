using Humans.Tickets.Contracts;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Base.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
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
    IConfiguration configuration,
    GatePinThrottle pinThrottle,
    IClock clock,
    ILogger<GateController> logger) : HumansControllerBase(users)
{
    private const string ScannerSessionKey = "GateScannerId";
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
        if (GetCurrentUserId() is not { } scanner)
            return Unauthorized();

        if (!ModelState.IsValid || idConfirmed is not { } confirmed)
        {
            logger.LogWarning("Rejected gate decision with missing or malformed values from scanner {ScannerId}", scanner);
            return BadRequest();
        }

        var decision = await gate.RecordDecisionAsync(
            new GateDecisionInput(barcode, confirmed, childWithAdult, laneId,
                ClientScanAt: null, Note: null, OverrideEarly: overrideEarly, SupervisorPin: supervisorPin),
            scanner, ct);

        return PartialView("_VerdictCard", GateScanCardViewModel.FromDecision(decision, barcode, childWithAdult));
    }

    [HttpGet("Claim")]
    public async Task<IActionResult> Claim(CancellationToken ct)
    {
        // The roster is opt-in (Gate:RosterTeamId); search still finds helpers who aren't rostered.
        var roster = await BuildRosterAsync(ct);
        return View(new GateClaimViewModel(roster));
    }

    [HttpPost("Claim")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(Guid userId, CancellationToken ct)
    {
        // The id comes from the form: it must be an active member, and a merged-away id fails closed rather than
        // stamping the session with an id the PIN store no longer recognises.
        var info = await UserService.GetUserInfoAsync(userId, ct);
        if (info is not { IsActive: true } || info.Id != userId)
            return RedirectToAction(nameof(Claim));

        var status = await gate.GetPinStatusAsync(userId, ct);
        return View("Pin", GatePinViewModel.ForClaim(userId, info.BurnerName, status));
    }

    [HttpPost("ClaimPin")]
    [Authorize(Policy = PolicyNames.GateAdmit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClaimPin(Guid userId, string? pin, CancellationToken ct)
    {
        // Guard before any enrol/verify/stamp: a direct POST with an arbitrary or inactive id must not mint a PIN.
        var info = await UserService.GetUserInfoAsync(userId, ct);
        if (info is not { IsActive: true } || info.Id != userId)
            return RedirectToAction(nameof(Claim));

        // Re-derive the mode server-side; never trust a client set/verify hint.
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
        // Clear attribution server-side: the session survives ~8h idle, so a walk-away would credit the next
        // person's scans to whoever last claimed.
        HttpContext.Session.Remove(ScannerSessionKey);
        return RedirectToAction(nameof(Claim));
    }

    // The route-locked kiosk can't reach /api/profiles/search. Burner-name matching only, never email.
    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length < 2)
            return Json(Array.Empty<HumanLookupSearchResult>());

        // A masked email tells same-name staffers apart; it is masked here, so the search itself stays email-free.
        var matches = (await UserService.SearchUsersAsync(query, PersonSearchFields.Name, 10, ct))
            .OrderByRelevance()
            .ToList();
        if (matches.Count == 0) return Json(new List<HumanLookupSearchResult>());
        var infos = await UserService.GetUserInfosAsync(matches.Select(m => m.UserId).Distinct().ToArray(), ct);
        var rows = matches.Select(m => new HumanLookupSearchResult(
            m.UserId, m.BurnerName, Detail: MaskEmail(PublicEmail(infos.GetValueOrDefault(m.UserId))),
            ProfilePictureUrl: m.ProfilePictureUrl)).ToList();
        return Json(rows);
    }

    // The one email safe to hint on a shared, role-less kiosk: verified and org-public, never a scoped address.
    private static string? PublicEmail(UserInfo? info) =>
        info is null
            ? null
            : info.UserEmails
                .Where(e => e.IsVerified && e.Visibility == ContactFieldVisibility.AllActiveProfiles)
                .OrderByDescending(e => e.IsPrimary)
                .Select(e => e.Email)
                .FirstOrDefault();

    // p•••h@gmail.com: recognisable to its owner, minimal to a bystander. Presentation only, never matching.
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
            .ToList();

        ViewData["TotalAdmitted"] = board.TotalAdmitted;
        ViewData["TotalScanned"] = board.TotalScanned;
        return View(rows);
    }

    // GET is the Staff PINs page; the POST is the save target of the /Settings#gate tab.
    [HttpGet("Admin")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    public IActionResult Admin() => View();

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

        await gate.SaveSettingsAsync(new GateSettingsDto(parsed.Value, model.MinorAgeThresholdYears), ct);
        SetSuccess("Gate settings saved.");
        return Redirect("/Settings#gate");
    }

    // Stored AdminEnrolled. Kiosk overrides use the shared Gate:SupervisorPin, not these PINs.
    [HttpPost("Admin/SetPin")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TicketsPolicies.Manage)]
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

    [HttpPost("Admin/ResetPin")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TicketsPolicies.Manage)]
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
            _ => View("Pin", GatePinViewModel.ForClaim(
                userId, name, status, "Pick a less obvious PIN — not 1234, 0000, or repeats")),
        };

    private IActionResult StampAndScan(Guid userId)
    {
        // Server-side, never from the request body, so attribution can't be forged.
        HttpContext.Session.SetString(ScannerSessionKey, userId.ToString());
        return RedirectToAction(nameof(Index));
    }

    // Claim PINs throttle per target user: the proxy collapses all kiosk traffic to one IP, so an IP key would let
    // one bad run freeze every claim.
    private static string UserKey(Guid userId) => $"u:{userId}";

    private int? ThrottleWait(string userKey) => pinThrottle.SecondsUntilRetry(userKey);

    private void RecordPinFailure(string userKey) => pinThrottle.RecordFailure(userKey);

    private void ResetPinThrottle(string userKey) => pinThrottle.Reset(userKey);

    private async Task<IReadOnlyList<GateRosterMember>> BuildRosterAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(configuration["Gate:RosterTeamId"], out var teamId))
            return [];

        var roster = await gate.GetShiftRosterAsync(teamId, ct);
        return roster.Select(r => new GateRosterMember(r.UserId, r.DisplayName)).ToList();
    }

}
