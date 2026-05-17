using System.Net;
using System.Text.Json;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models.Mailer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers.Mailer;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Mailer/Admin")]
public sealed class MailerAdminController : HumansControllerBase
{
    private readonly IMailerLiteService _ml;
    private readonly IMailerImportService _import;
    private readonly IMailerAudienceSyncService _audienceSync;
    private readonly IReadOnlyList<IMailerAudience> _audiences;
    private readonly IUserService _users;
    private readonly ICommunicationPreferenceService _prefs;
    private readonly IAuditLogService _audit;
    private readonly ILogger<MailerAdminController> _logger;

    public MailerAdminController(
        IMailerLiteService ml,
        IMailerImportService import,
        IMailerAudienceSyncService audienceSync,
        IEnumerable<IMailerAudience> audiences,
        IUserService users,
        ICommunicationPreferenceService prefs,
        IAuditLogService audit,
        ILogger<MailerAdminController> logger)
        : base(users)
    {
        _ml = ml;
        _import = import;
        _audienceSync = audienceSync;
        _audiences = audiences.ToList();
        _users = users;
        _prefs = prefs;
        _audit = audit;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        MailerLiteAccountSummary? summary = null;
        IReadOnlyList<MailerLiteGroup>? groups = null;
        DriftReport? drift = null;
        string? mlError = null;

        try
        {
            summary = await _ml.GetAccountSummaryAsync(ct);
            groups = await _ml.ListGroupsAsync(ct);
            drift = await ComputeDriftAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("MailerLite API call failed: {StatusCode} {Message}", ex.StatusCode, ex.Message);
            mlError = FormatMailerLiteError(ex);
        }

        var mlContacts = (await _users.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Count(u => u.ContactSource == ContactSource.MailerLite);
        var optedIn = await _prefs.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false, ct);
        var optedOut = await _prefs.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true, ct);

        var recent = await _audit.GetFilteredEntriesAsync(
            actions: [AuditAction.MailerLiteReconciliationCompleted],
            limit: 1,
            ct: ct);
        var last = recent.FirstOrDefault();

        IReadOnlyList<AudienceCardRow> audienceRows;
        try
        {
            var stats = await _audienceSync.ComputeAllStatsAsync(ct);
            audienceRows = stats.Select(s => new AudienceCardRow(
                s.Key, s.DisplayName, s.MailerLiteGroupName,
                s.Candidates, s.ExcludedUnsubscribed, s.CurrentlyInGroup,
                s.LastSyncAt, s.LastSyncSummary)).ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Audience stats failed");
            audienceRows = [];
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Audience stats timed out");
            audienceRows = [];
        }

        var vm = new MailerDashboardViewModel(
            summary, groups, mlContacts, optedIn, optedOut,
            last?.OccurredAt, last?.Description, drift, mlError,
            _ml.LastFetchedAt,
            audienceRows);
        return View("~/Views/Mailer/Admin/Index.cshtml", vm);
    }

    [HttpPost("Audiences/{key}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncAudience(string key, CancellationToken ct)
    {
        var audience = _audiences.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
        if (audience is null) return NotFound();

        try
        {
            var actor = await GetCurrentUserInfoAsync();
            var result = await _audienceSync.SyncAsync(audience, actor?.Id, ct);
            TempData["Banner"] = $"{audience.DisplayName}: {result.FormatSummary()}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Audience sync failed for {Audience}", key);
            TempData["Banner"] = $"{audience.DisplayName}: sync failed — {ex.Message}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout surfaces as TaskCanceledException when caller didn't cancel.
            _logger.LogWarning("Audience sync timed out for {Audience}", key);
            TempData["Banner"] = $"{audience.DisplayName}: sync timed out. Try again shortly.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Refresh")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        try
        {
            await _ml.RefreshAsync(ct);
            TempData["Banner"] = "MailerLite cache refreshed.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("MailerLite refresh failed: {StatusCode} {Message}", ex.StatusCode, ex.Message);
            TempData["Banner"] = "Refresh failed: " + FormatMailerLiteError(ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout surfaces as TaskCanceledException when caller didn't cancel.
            _logger.LogWarning("MailerLite refresh timed out");
            TempData["Banner"] = "Refresh failed: MailerLite request timed out. Try again shortly.";
        }
        return RedirectToAction(nameof(Index));
    }

    private static string FormatMailerLiteError(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "MailerLite rejected the API key (401). Check MAILERLITE_API_KEY on /Admin/Configuration.",
        HttpStatusCode.Forbidden => "MailerLite API key lacks required permissions (403).",
        HttpStatusCode.TooManyRequests => "MailerLite rate limit hit (429). Try again shortly.",
        null => $"MailerLite call failed before getting a response: {ex.Message}",
        _ => $"MailerLite API returned {(int)ex.StatusCode} {ex.StatusCode}.",
    };

    [HttpPost("Import/Commit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Commit([FromForm] int? maxPerOutcome, CancellationToken ct)
    {
        var fresh = await _import.BuildPlanAsync(ct);

        if (TempData["PlanCountsSnapshot"] is string snapshotJson)
        {
            var snapshot = JsonSerializer.Deserialize<ImportPlanCounts>(snapshotJson);
            if (snapshot is not null && DriftedMoreThanTenPercent(snapshot, fresh.Counts))
            {
                TempData["Banner"] = "Plan changed since preview — review and re-confirm.";
                return RedirectToAction(nameof(Import));
            }
        }

        var result = await _import.ApplyAsync(fresh, maxPerOutcome, ct);
        TempData["Banner"] = result.FormatSummary();
        return RedirectToAction(nameof(Index));
    }

    private static bool DriftedMoreThanTenPercent(ImportPlanCounts a, ImportPlanCounts b)
    {
        bool D(int prev, int now)
        {
            if (prev == 0) return now > 0;
            return Math.Abs(now - prev) / (double)prev > 0.10;
        }
        return D(a.CreateNewHuman, b.CreateNewHuman)
            || D(a.ReplaceUnverifiedEmail, b.ReplaceUnverifiedEmail)
            || D(a.VerifiedPrefsAlreadyMatch, b.VerifiedPrefsAlreadyMatch)
            || D(a.VerifiedFlipToOptIn, b.VerifiedFlipToOptIn)
            || D(a.VerifiedFlipToOptOut, b.VerifiedFlipToOptOut)
            || D(a.VerifiedKeepHumansPref, b.VerifiedKeepHumansPref)
            || D(a.AmbiguousMultipleVerified, b.AmbiguousMultipleVerified)
            || D(a.UnconfirmedSkipped, b.UnconfirmedSkipped);
    }

    [HttpGet("Import")]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        var plan = await _import.BuildPlanAsync(ct);
        var rows = ProjectRows(plan);

        // Snapshot counts in TempData for the >10% delta check on Commit.
        TempData["PlanCountsSnapshot"] = JsonSerializer.Serialize(plan.Counts);

        return View("~/Views/Mailer/Admin/Import.cshtml",
            new MailerImportPreviewViewModel(plan, rows));
    }

    private static IReadOnlyList<SubscriberDecisionRow> ProjectRows(ImportPlan plan) =>
        plan.Decisions.Select(d => new SubscriberDecisionRow(
            Email: d.Email,
            MlStatus: d.Status,
            MlLastActionAt: null,
            MatchedUserId: d.TargetUserId,
            Outcome: d.Outcome)).ToList();

    private async Task<DriftReport> ComputeDriftAsync(CancellationToken ct)
    {
        var plan = await _import.BuildPlanAsync(ct);

        int humansOutMlIn = 0;
        foreach (var d in plan.Decisions.Where(d => d.Outcome
            is SubscriberOutcome.VerifiedPrefsAlreadyMatch
            or SubscriberOutcome.VerifiedFlipToOptIn
            or SubscriberOutcome.VerifiedFlipToOptOut
            or SubscriberOutcome.VerifiedKeepHumansPref))
        {
            if (d.TargetUserId is not Guid uid) continue;
            if (!string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase)) continue;
            var isOptedOut = await _prefs.IsOptedOutAsync(uid, MessageCategory.Marketing, ct);
            if (isOptedOut) humansOutMlIn++;
        }

        // Humans-opted-in / ML-absent half left null — adding a new service method
        // for one admin page is durable debt per memory/architecture/interface-method-additions-are-debt.md.
        int? humansInMlAbsent = null;

        return new DriftReport(
            HumansOptedOutMlActive: humansOutMlIn,
            HumansOptedInMlAbsent: humansInMlAbsent);
    }
}
