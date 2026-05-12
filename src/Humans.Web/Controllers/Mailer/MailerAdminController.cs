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

namespace Humans.Web.Controllers.Mailer;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Mailer/Admin")]
public sealed class MailerAdminController : HumansControllerBase
{
    private readonly IMailerLiteService _ml;
    private readonly IMailerImportService _import;
    private readonly IUserService _users;
    private readonly ICommunicationPreferenceService _prefs;
    private readonly IForgottenEmailService _forgotten;
    private readonly IAuditLogService _audit;

    public MailerAdminController(
        IMailerLiteService ml,
        IMailerImportService import,
        IUserService users,
        ICommunicationPreferenceService prefs,
        IForgottenEmailService forgotten,
        IAuditLogService audit,
        UserManager<User> userManager)
        : base(userManager)
    {
        _ml = ml;
        _import = import;
        _users = users;
        _prefs = prefs;
        _forgotten = forgotten;
        _audit = audit;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var summary = await _ml.GetAccountSummaryAsync(ct);
        var groups = await _ml.ListGroupsAsync(ct);
        var mlContacts = await _users.GetCountByContactSourceAsync(ContactSource.MailerLite, ct);
        var optedIn = await _prefs.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false, ct);
        var optedOut = await _prefs.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true, ct);
        var forgottenCount = await _forgotten.CountAsync(ct);

        var recent = await _audit.GetFilteredEntriesAsync(
            actions: new[] { AuditAction.MailerLiteReconciliationCompleted },
            limit: 1,
            ct: ct);
        var last = recent.FirstOrDefault();

        var drift = await ComputeDriftAsync(ct);

        var vm = new MailerDashboardViewModel(
            summary, groups, mlContacts, optedIn, optedOut, forgottenCount,
            last?.OccurredAt, last?.Description, drift);
        return View("~/Views/Mailer/Admin/Index.cshtml", vm);
    }

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
            || D(a.SkippedForgotten, b.SkippedForgotten)
            || D(a.SkippedAmbiguous, b.SkippedAmbiguous)
            || D(a.SkippedUnconfirmed, b.SkippedUnconfirmed);
    }

    [HttpGet("Import")]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        var plan = await _import.BuildPlanAsync(ct);
        var rows = await ProjectRowsAsync(plan, ct);

        // Snapshot counts in TempData for the >10% delta check on Commit (Task 27).
        TempData["PlanCountsSnapshot"] = JsonSerializer.Serialize(plan.Counts);

        return View("~/Views/Mailer/Admin/Import.cshtml",
            new MailerImportPreviewViewModel(plan, rows));
    }

    private async Task<IReadOnlyList<SubscriberDecisionRow>> ProjectRowsAsync(
        ImportPlan plan, CancellationToken ct)
    {
        var matchedUserIds = plan.Decisions
            .Where(d => d.TargetUserId is not null)
            .Select(d => d.TargetUserId!.Value)
            .Distinct()
            .ToList();
        var users = matchedUserIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _users.GetDisplayNamesByIdsAsync(matchedUserIds, ct);

        return plan.Decisions.Select(d => new SubscriberDecisionRow(
            EmailRedacted: Redact(d.Email),
            EmailFull: d.Email,
            MlStatus: d.Status,
            MlLastActionAt: null,
            MatchedDisplayName: d.TargetUserId is Guid uid && users.TryGetValue(uid, out var n) ? n : null,
            MatchedUserId: d.TargetUserId,
            Outcome: d.Outcome)).ToList();
    }

    private static string Redact(string email)
    {
        var at = email.IndexOf('@');
        if (at < 0 || at < 2) return email;
        var local = email[..at];
        var domain = email[(at + 1)..];
        return $"{local[..2]}***@{domain[..Math.Min(3, domain.Length)]}***";
    }

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

        // The "Forgotten (GDPR) but still active in ML" dashboard tile counts
        // only ML-active rows. SkippedForgotten lumps in non-active statuses
        // (unsubscribed/bounced/junk) — including those would overstate GDPR
        // drift risk and trigger spurious admin alarms.
        var forgottenButMlActive = plan.Decisions.Count(d =>
            d.Outcome == SubscriberOutcome.ForgottenSkipped
            && string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase));

        return new DriftReport(
            HumansOptedOutMlActive: humansOutMlIn,
            HumansOptedInMlAbsent: humansInMlAbsent,
            ForgottenButMlActive: forgottenButMlActive);
    }
}
