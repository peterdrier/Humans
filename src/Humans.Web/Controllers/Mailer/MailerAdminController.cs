using System.Net;
using System.Text.Json;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models.Mailer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Controllers.Mailer;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Mailer/Admin")]
public sealed class MailerAdminController : HumansControllerBase
{
    private readonly IMailerLiteService _ml;
    private readonly IMailerImportService _import;
    private readonly IUserService _users;
    private readonly ICommunicationPreferenceService _prefs;
    private readonly IAuditLogService _audit;
    private readonly ILogger<MailerAdminController> _logger;

    public MailerAdminController(
        IMailerLiteService ml,
        IMailerImportService import,
        IUserService users,
        ICommunicationPreferenceService prefs,
        IAuditLogService audit,
        ILogger<MailerAdminController> logger,
        UserManager<User> userManager)
        : base(userManager)
    {
        _ml = ml;
        _import = import;
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

        var mlContacts = await _users.GetCountByContactSourceAsync(ContactSource.MailerLite, ct);
        var optedIn = await _prefs.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false, ct);
        var optedOut = await _prefs.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true, ct);

        var recent = await _audit.GetFilteredEntriesAsync(
            actions: new[] { AuditAction.MailerLiteReconciliationCompleted },
            limit: 1,
            ct: ct);
        var last = recent.FirstOrDefault();

        var vm = new MailerDashboardViewModel(
            summary, groups, mlContacts, optedIn, optedOut,
            last?.OccurredAt, last?.Description, drift, mlError);
        return View("~/Views/Mailer/Admin/Index.cshtml", vm);
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
    public async Task<IActionResult> Commit(CancellationToken ct)
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

        var result = await _import.ApplyAsync(fresh, ct);
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
        return D(a.WillCreateContact, b.WillCreateContact)
            || D(a.WillAttachWithFlip, b.WillAttachWithFlip)
            || D(a.WillAttachConfirmOnly, b.WillAttachConfirmOnly)
            || D(a.WillKeepHumansState, b.WillKeepHumansState)
            || D(a.WillDeleteUnverifiedAndCreate, b.WillDeleteUnverifiedAndCreate)
            || D(a.SkippedAmbiguous, b.SkippedAmbiguous)
            || D(a.SkippedUnconfirmed, b.SkippedUnconfirmed);
    }

    [HttpGet("Import")]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        var plan = await _import.BuildPlanAsync(ct);
        var rows = ProjectRows(plan);

        // Snapshot counts in TempData for the >10% delta check on Commit (Task 27).
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
        foreach (var d in plan.Decisions.Where(d => d.Outcome == SubscriberOutcome.AttachVerified
                                                || d.Outcome == SubscriberOutcome.AttachVerifiedConflictKept))
        {
            if (d.TargetUserId is not Guid uid) continue;
            if (!string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase)) continue;
            var isOptedOut = await _prefs.IsOptedOutAsync(uid, MessageCategory.Marketing, ct);
            if (isOptedOut) humansOutMlIn++;
        }

        int? humansInMlAbsent = null; // TODO: cross-reference once IUserEmailService supports it

        return new DriftReport(
            HumansOptedOutMlActive: humansOutMlIn,
            HumansOptedInMlAbsent: humansInMlAbsent);
    }
}
