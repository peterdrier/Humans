using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Expenses;

/// <summary>
/// Application-layer orchestrator for Expense Reports. Coordinates
/// <see cref="IExpenseRepository"/>, audit logging, IBAN snapshots, and
/// cross-section reads via interfaces — never imports EF Core directly.
/// </summary>
public sealed class ExpenseReportService : IExpenseReportService, IUserDataContributor
{
    private readonly IExpenseRepository _repo;
    private readonly IBudgetService _budgetService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<ExpenseReportService> _logger;

    public ExpenseReportService(
        IExpenseRepository repo,
        IBudgetService budgetService,
        ITeamService teamService,
        IUserService userService,
        IProfileService profileService,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<ExpenseReportService> logger)
    {
        _repo = repo;
        _budgetService = budgetService;
        _teamService = teamService;
        _userService = userService;
        _profileService = profileService;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    // ─────────────────────────────── Reads ───────────────────────────────────

    public async Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdWithLinesAsync(id, ct);
        return report is null ? null : ToDto(report);
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        var reports = await _repo.GetForSubmitterAsync(submitterUserId, ct);
        return reports.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        CancellationToken ct = default)
    {
        var reports = await _repo.GetForReviewQueueAsync(ct);
        return reports.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetApprovedUnpaidAsync(
        CancellationToken ct = default)
    {
        var approved = await _repo.GetByStatusAsync(ExpenseReportStatus.Approved, ct);
        var sepaSent = await _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, ct);
        return approved.Concat(sepaSent)
            .OrderBy(r => r.ApprovedAt ?? r.CreatedAt)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        // Resolve category ids the coordinator is responsible for via budget + team services.
        var teamIds = await _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorUserId, ct);
        if (teamIds.Count == 0) return [];

        var year = await _budgetService.GetActiveYearAsync();
        if (year is null) return [];


        var categoryIds = year.Groups
            .SelectMany(g => g.Categories)
            .Where(c => c.TeamId.HasValue && teamIds.Contains(c.TeamId.Value))
            .Select(c => c.Id)
            .ToList();

        if (categoryIds.Count == 0) return [];

        var reports = await _repo.GetByCategoryIdsAndStatusAsync(categoryIds,
            ExpenseReportStatus.Submitted, ct);
        return reports.Select(ToDto).ToList();
    }

    // ──────────────────────────── Draft CRUD ─────────────────────────────────

    public async Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var year = await _budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var now = _clock.GetCurrentInstant();
        var report = new ExpenseReport
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterUserId,
            BudgetCategoryId = category.Id,
            BudgetYearId = year.Id,
            Status = ExpenseReportStatus.Draft,
            Note = note,
            PayeeName = "",
            PayeeIban = "",
            Total = 0m,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _repo.AddDraftAsync(report, ct);
        return report.Id;
    }

    public async Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var existing = await _repo.GetByIdAsync(reportId, ct);
        if (existing is null) throw new InvalidOperationException("Report not found.");
        if (existing.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can update a draft.");
        if (existing.Status != ExpenseReportStatus.Draft)
            throw new InvalidOperationException("Only Draft reports can be updated.");

        var year = await _budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var updated = new ExpenseReport
        {
            Id = reportId,
            BudgetCategoryId = category.Id,
            BudgetYearId = year.Id,
            Note = note,
            UpdatedAt = _clock.GetCurrentInstant()
        };
        await _repo.UpdateDraftAsync(updated, ct);
    }

    // ────────────────────────── Line Methods ─────────────────────────────────

    public async Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount
        };
        var ok = await _repo.AddLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to add line.");
        return line.Id;
    }

    public async Task UpdateLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = new ExpenseLine
        {
            Id = lineId,
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount
        };
        var ok = await _repo.UpdateLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to update line.");
    }

    public async Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var ok = await _repo.RemoveLineAsync(reportId, lineId, ct);
        if (!ok) throw new InvalidOperationException("Failed to remove line.");
    }

    public async Task AttachToLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, Guid attachmentId,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        await _repo.SetLineAttachmentAsync(lineId, attachmentId, ct);
    }

    // ───────────────────────── Submit / Withdraw ──────────────────────────────

    public async Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdWithLinesAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can submit.");
        if (report.Status != ExpenseReportStatus.Draft) return false;

        // Validate: at least 1 line
        if (!report.Lines.Any())
            throw new InvalidOperationException("Report must have at least one line.");

        // Validate: every line has an attachment
        if (report.Lines.Any(l => l.AttachmentId is null))
            throw new InvalidOperationException("Every line must have an attachment before submitting.");

        // Validate + snapshot IBAN
        var profile = await _profileService.GetProfileAsync(submitterUserId, ct);
        if (profile?.Iban is null)
            throw new InvalidOperationException("Submitter must have an IBAN set on their profile.");

        var user = await _userService.GetByIdAsync(submitterUserId, ct);
        var payeeName = user?.DisplayName ?? "";
        var payeeIban = profile.Iban;

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.SubmitAsync(reportId, payeeName, payeeIban, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", reportId,
            $"Submitted expense report.",
            submitterUserId);

        return true;
    }

    public async Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can withdraw.");

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.WithdrawAsync(reportId, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseWithdraw,
            "ExpenseReport", reportId,
            $"Withdrew expense report.",
            submitterUserId);

        return true;
    }

    // ─────────────────────── Coordinator Endorsement ─────────────────────────

    public async Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseEndorse,
            "ExpenseReport", reportId,
            $"Coordinator endorsed expense report.",
            coordinatorUserId);

        return true;
    }

    public async Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseCoordinatorReject,
            "ExpenseReport", reportId,
            $"Coordinator rejected expense report: {reason}",
            coordinatorUserId);

        return true;
    }

    // ────────────────────── Finance Approve / Reject ──────────────────────────

    public async Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var outboxEventId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var ok = await _repo.ApproveAsync(reportId, actorUserId, overrideCategoryId, now, outboxEventId, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseApprove,
            "ExpenseReport", reportId,
            $"Finance approved expense report.",
            actorUserId);

        if (overrideCategoryId.HasValue && overrideCategoryId.Value != report.BudgetCategoryId)
        {
            await _auditLogService.LogAsync(
                AuditAction.ExpenseCategoryOverride,
                "ExpenseReport", reportId,
                $"Category overridden during approval to {overrideCategoryId.Value}.",
                actorUserId);
        }

        return true;
    }

    public async Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.FinanceRejectAsync(reportId, actorUserId, reason, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseReject,
            "ExpenseReport", reportId,
            $"Finance rejected expense report: {reason}",
            actorUserId);

        return true;
    }

    public async Task<bool> CategoryOverrideAsync(
        Guid reportId, Guid actorUserId, Guid newCategoryId,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var outboxEventId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var ok = await _repo.CategoryOverrideAsync(reportId, actorUserId, newCategoryId, now, outboxEventId, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseCategoryOverride,
            "ExpenseReport", reportId,
            $"Category overridden post-approval to {newCategoryId}.",
            actorUserId);

        return true;
    }

    // ─────────────────────────── SEPA + Paid ─────────────────────────────────

    public async Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (reportIds.Count == 0) return [];

        var now = _clock.GetCurrentInstant();
        var flippedIds = await _repo.MarkSepaSentAsync(reportIds, now, ct);

        // Audit one entry per report that actually flipped — never for ids the
        // repo skipped (e.g. status != Approved).
        foreach (var id in flippedIds)
        {
            await _auditLogService.LogAsync(
                AuditAction.ExpenseSepaSent,
                "ExpenseReport", id,
                "Marked as SEPA sent.",
                actorUserId);
        }

        return flippedIds;
    }

    public async Task<bool> MarkPaidAsync(
        Guid reportId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var ok = await _repo.MarkPaidAsync(reportId, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpensePaid,
            "ExpenseReport", reportId,
            "Marked as paid.",
            "ExpensePaidJob");

        return true;
    }

    // ─────────────────────── Coordinator Detection ───────────────────────────

    /// <inheritdoc/>
    public Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default)
    {
        // TODO: Wire up real coordinator detection once ITeamService exposes TeamInfo with Coordinators.
        // For now returns false — submitter→FinanceAdmin path is the only active workflow.
        return Task.FromResult(false);
    }

    // ─────────────────────────── Private Helpers ─────────────────────────────

    /// <summary>
    /// Loads the report and enforces that: the caller is the submitter, and
    /// the report is in a state that allows line/attachment edits
    /// ({Draft, Submitted, CoordinatorEndorsed}).
    /// </summary>
    private async Task<ExpenseReport> RequireEditableReportAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct)
    {
        var report = await _repo.GetByIdAsync(reportId, ct)
            ?? throw new InvalidOperationException("Report not found.");
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can edit lines.");
        if (report.Status is not (ExpenseReportStatus.Draft
                                  or ExpenseReportStatus.Submitted
                                  or ExpenseReportStatus.CoordinatorEndorsed))
            throw new InvalidOperationException(
                $"Lines cannot be edited when the report is in status {report.Status}.");
        return report;
    }

    /// <summary>
    /// Checks the actor is a coordinator of the team that owns the category.
    /// Throws <see cref="UnauthorizedAccessException"/> if not.
    /// </summary>
    private async Task RequireCoordinatorForCategoryAsync(
        Guid categoryId, Guid actorUserId, CancellationToken ct)
    {
        var category = await _budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null)
            throw new InvalidOperationException("Budget category not found.");
        if (!category.TeamId.HasValue)
            throw new UnauthorizedAccessException(
                "Category has no owning team; coordinator endorsement is not valid.");
        var isCoordinator = await _teamService.IsUserCoordinatorOfTeamAsync(
            category.TeamId.Value, actorUserId, ct);
        if (!isCoordinator)
            throw new UnauthorizedAccessException("Actor is not a coordinator of the category's team.");
    }

    private static ExpenseReportDto ToDto(ExpenseReport r) => new()
    {
        Id = r.Id,
        SubmitterUserId = r.SubmitterUserId,
        BudgetCategoryId = r.BudgetCategoryId,
        BudgetYearId = r.BudgetYearId,
        Status = r.Status,
        Note = r.Note,
        PayeeName = r.PayeeName,
        PayeeIban = r.PayeeIban,
        Total = r.Total,
        SubmittedAt = r.SubmittedAt,
        CoordinatorEndorsedByUserId = r.CoordinatorEndorsedByUserId,
        CoordinatorEndorsedAt = r.CoordinatorEndorsedAt,
        ApprovedByUserId = r.ApprovedByUserId,
        ApprovedAt = r.ApprovedAt,
        SepaSentAt = r.SepaSentAt,
        PaidAt = r.PaidAt,
        LastRejectionReason = r.LastRejectionReason,
        LastRejectedByUserId = r.LastRejectedByUserId,
        LastRejectedAt = r.LastRejectedAt,
        HoldedDocId = r.HoldedDocId,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Lines = r.Lines.Select(ToDto).ToList()
    };

    private static ExpenseLineDto ToDto(ExpenseLine l) => new()
    {
        Id = l.Id,
        ExpenseReportId = l.ExpenseReportId,
        Description = l.Description,
        Amount = l.Amount,
        AttachmentId = l.AttachmentId,
        Attachment = l.Attachment is null ? null : ToDto(l.Attachment),
        SortOrder = l.SortOrder
    };

    private static ExpenseAttachmentDto ToDto(ExpenseAttachment a) => new()
    {
        Id = a.Id,
        OriginalFileName = a.OriginalFileName,
        Extension = a.Extension,
        ContentType = a.ContentType,
        SizeBytes = a.SizeBytes,
        UploadedByUserId = a.UploadedByUserId,
        UploadedAt = a.UploadedAt
    };

    // ─────────────────────── IUserDataContributor (GDPR) ─────────────────────

    /// <summary>
    /// Returns the user's expense reports (with lines and attachment metadata —
    /// no bytes), a masked IBAN snapshot, and expense-related audit-log entries.
    /// Chain-follows merge tombstones so a fold-target's export includes reports
    /// submitted under merged source ids.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var sourceIds = await _userService.GetMergedSourceIdsAsync(userId, ct);

        // Collect all ids whose reports we should include
        var allIds = new List<Guid>(sourceIds.Count + 1);
        allIds.AddRange(sourceIds);
        allIds.Add(userId);

        // Fetch reports for all ids, then re-fetch each with lines + attachments.
        // GDPR export is infrequent, so N+1 is acceptable here.
        var reportIds = new List<Guid>();
        foreach (var id in allIds)
        {
            var reports = await _repo.GetForSubmitterAsync(id, ct);
            reportIds.AddRange(reports.Select(r => r.Id));
        }

        var allReports = new List<ExpenseReport>();
        foreach (var reportId in reportIds)
        {
            var full = await _repo.GetByIdWithLinesAsync(reportId, ct);
            if (full is not null)
                allReports.Add(full);
        }

        // Fetch current IBAN from profile (masked per spec)
        var profile = await _profileService.GetProfileAsync(userId, ct);
        var maskedIban = string.IsNullOrEmpty(profile?.Iban)
            ? null
            : IbanFormatter.Mask(profile.Iban);

        // Expense-related audit actions (this user as actor or subject)
        var expenseActions = new List<AuditAction>
        {
            AuditAction.ExpenseSubmit,
            AuditAction.ExpenseEndorse,
            AuditAction.ExpenseCoordinatorReject,
            AuditAction.ExpenseApprove,
            AuditAction.ExpenseReject,
            AuditAction.ExpenseWithdraw,
            AuditAction.ExpenseCategoryOverride,
            AuditAction.ExpenseSepaSent,
            AuditAction.ExpensePaid,
            AuditAction.IbanSet,
            AuditAction.IbanRemove,
            AuditAction.IbanReveal,
        };

        var auditEntries = await _auditLogService.GetFilteredEntriesAsync(
            userId: userId,
            actions: expenseActions,
            limit: 10_000,
            ct: ct);

        var shapedReports = allReports
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.Note,
                r.PayeeName,
                PayeeIban = IbanFormatter.Mask(r.PayeeIban),
                r.Total,
                r.SubmittedAt,
                r.ApprovedAt,
                r.SepaSentAt,
                r.PaidAt,
                r.CreatedAt,
                Lines = r.Lines.Select(l => new
                {
                    l.Id,
                    l.Description,
                    l.Amount,
                    l.SortOrder,
                    Attachment = l.Attachment is null
                        ? null
                        : new
                        {
                            l.Attachment.OriginalFileName,
                            l.Attachment.ContentType,
                            l.Attachment.SizeBytes,
                        }
                }).ToList()
            }).ToList();

        var shapedAudit = auditEntries
            .Select(e => new
            {
                e.Action,
                e.EntityType,
                e.EntityId,
                e.Description,
                OccurredAt = e.OccurredAt.ToInvariantInstantString()
            }).ToList();

        return
        [
            new UserDataSlice(GdprExportSections.ExpenseReports,
                shapedReports.Count > 0 ? shapedReports : null),
            new UserDataSlice(GdprExportSections.ExpenseAuditLog,
                shapedAudit.Count > 0
                    ? new { MaskedIban = maskedIban, Entries = shapedAudit }
                    : (object?)null),
        ];
    }
}
