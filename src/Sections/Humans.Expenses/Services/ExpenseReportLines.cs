using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportLines(
    IExpenseRepository repo,
    ExpenseReportAccess reportAccess,
    ExpenseAttachments attachments,
    ExpenseReportAudit reportAudit,
    ILogger<ExpenseReportService> logger,
    TravelReimbursementConfig travel) : IApplicationService
{
    private readonly TravelReimbursementConfig _travel = travel;

    private async Task<(ExpenseAddLineResult Result, ExpenseReportDto? Report)> AddLineWithoutAuditAsync(
        Guid reportId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        string description, decimal amount, ExpenseLineType lineType,
        Guid? parentLineId, CancellationToken ct)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
        if (refusal is not null) return (new(false, refusal.Error, null), null);
        var report = editableReport!;

        if (parentLineId is { } parentId)
        {
            // A proof row is a Receipt backing an Invoice line on the same report. One level only.
            if (lineType != ExpenseLineType.Receipt)
                return (new(false, ExpenseMutationResult.Failure("Expenses_Validation_ProofRowsMustBeReceipts").Error, null), null);
            var parent = report.Lines.FirstOrDefault(l => l.Id == parentId);
            if (parent is null) return (new(false, ExpenseMutationResult.Failure("Expenses_Validation_ParentLineNotFound").Error, null), null);
            if (parent.LineType != ExpenseLineType.Invoice)
                return (new(false, ExpenseMutationResult.Failure("Expenses_Validation_ProofRowsRequireInvoice").Error, null), null);
        }

        var line = new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount,
            LineType = lineType,
            ParentLineId = parentLineId
        };
        try
        {
            var ok = await repo.AddLineAsync(reportId, line, ct);
            if (!ok) throw new InvalidOperationException("Failed to add line.");
        }
        catch
        {
            await TryRollbackAddedLineAsync(reportId, line.Id);
            throw;
        }

        return (new(true, null, line.Id), report);
    }

    internal async Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default)
    {
        ExpenseAddLineResult Reject(ExpenseError refusal)
        {
            logger.LogWarning("Error adding line to report {ReportId}: {Reason}", reportId, refusal.ResourceKey);
            return new(false, refusal, null);
        }

        try
        {
            // This path takes free-text amounts; travel lines are computed.
            if (lineType is not (ExpenseLineType.Receipt or ExpenseLineType.Invoice))
                return Reject(new("Expenses_Validation_OnlyReceiptAndInvoiceLines", []));
            if (decimal.Round(amount, 2) != amount)
                return Reject(new("Expenses_Validation_AmountPrecision", []));
            // Validate the file before creating anything, so a bad upload leaves no half-made line.
            if (file is not null && ExpenseAttachments.ValidateAttachmentUpload(file.FileName, file.ContentType, file.Content).Refusal is { } uploadRefusal)
                return Reject(uploadRefusal);

            var (added, report) = await AddLineWithoutAuditAsync(
                reportId, allowedStatuses,
                description, amount, lineType, parentLineId, ct);
            if (added.Error is { } addRefusal) return Reject(addRefusal);
            var lineId = added.LineId!.Value;
            ExpenseError? attachmentRefusal = null;
            try
            {
                if (file is not null)
                    attachmentRefusal = await attachments.StoreAttachmentAsync(
                        reportAccess, reportId, actorUserId, allowedStatuses, lineId, file.FileName, file.ContentType, file.Content, ct);
                if (attachmentRefusal is null)
                    await reportAudit.AuditOnBehalfEditAsync(report!, actorUserId,
                        $"Added {(parentLineId is null ? "line" : "proof row")} \"{description}\" €{amount}", CancellationToken.None);
            }
            catch
            {
                // The form retries the whole add. Undo the new line and any linked upload before failing.
                await TryRollbackAddedLineAsync(reportId, lineId);
                throw;
            }
            if (attachmentRefusal is not null)
            {
                if (!await TryRollbackAddedLineAsync(reportId, lineId))
                    return new ExpenseAddLineResult(false, null, null);
                return Reject(attachmentRefusal);
            }
            return new ExpenseAddLineResult(true, null, lineId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding line to report {ReportId}", reportId);
            return new ExpenseAddLineResult(false, null, null);
        }
    }

    private async Task<bool> TryRollbackAddedLineAsync(Guid reportId, Guid lineId)
    {
        ExpenseReportDto? report = null;
        try
        {
            report = await repo.GetByIdAsync(reportId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read attachments before rolling back new line {LineId}", lineId);
        }
        var candidates = report?.Lines.Where(line => line.Id == lineId || line.ParentLineId == lineId)
            .Select(line => line.Attachment).OfType<ExpenseAttachmentDto>()
            .Select(attachment => (attachment.Id, attachment.Extension)).ToList() ?? [];
        IReadOnlyList<ExpenseAttachment>? removed = null;
        try
        {
            try
            {
                removed = await repo.RemoveLineAsync(reportId, lineId, CancellationToken.None);
            }
            finally
            {
                // Retain the pre-delete ids too: compensation can itself commit before its acknowledgement fails.
                await attachments.CleanupUnlinkedAttachmentsAsync(
                    removed is null ? candidates : removed.Select(a => (a.Id, a.Extension)), lineId);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not roll back new expense line {LineId} on report {ReportId}", lineId, reportId);
            return false;
        }
    }

    private async Task<ExpenseMutationResult> AddComputedLineAsync(
        Guid reportId, Guid actorUserId, string description, decimal amount, ExpenseLineType lineType,
        CancellationToken ct)
    {
        var (added, report) = await AddLineWithoutAuditAsync(
            reportId, [ExpenseReportStatus.Draft], description, amount, lineType, null, ct);
        if (added.Error is not null) return new(false, added.Error);
        await reportAudit.AuditOnBehalfEditAsync(report!, actorUserId, $"Added line \"{description}\" €{amount}", ct);
        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default)
    {
        var rate = _travel.MileageRatePerKm;
        var amount = Math.Round(km * rate, 2, MidpointRounding.AwayFromZero);
        var description =
            $"{origin.Trim()} to {destination.Trim()}, " +
            $"{km.ToString("0.#", CultureInfo.InvariantCulture)} km @ " +
            $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
            $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        return await AddComputedLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.Mileage, ct);
    }

    internal async Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default)
    {
        var rate = kind == PerDiemKind.Overnight ? _travel.PerDiemOvernightRate : _travel.PerDiemDayTripRate;
        var amount = Math.Round(days * rate, 2, MidpointRounding.AwayFromZero);
        var kindLabel = kind == PerDiemKind.Overnight ? "overnight" : "day-trip";
        var dayWord = days == 1 ? "day" : "days";
        var description =
            $"Per diem: {days} {dayWord} {kindLabel} @ " +
            $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
            $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(note))
            description += $" — {note.Trim()}";
        return await AddComputedLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.PerDiem, ct);
    }

    internal async Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        var report = editableReport!;

        var existing = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (existing is null) return ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport");
        // Travel lines carry computed amounts and waive the receipt on that basis; a free-text edit would allow an
        // arbitrary unreceipted claim. Remove and re-add to recompute.
        if (existing.LineType is ExpenseLineType.Mileage or ExpenseLineType.PerDiem)
            return ExpenseMutationResult.Failure("Expenses_Validation_TravelLinesComputedCannotEdit");

        if (decimal.Round(amount, 2) != amount)
            return ExpenseMutationResult.Failure("Expenses_Validation_AmountPrecision");

        var line = new ExpenseLine
        {
            Id = lineId,
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount
        };
        var ok = await repo.UpdateLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to update line.");

        await reportAudit.AuditOnBehalfEditAsync(report, actorUserId,
            $"Updated line \"{existing.Description}\" €{existing.Amount} to \"{description}\" €{amount}", ct);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, Guid lineId,
        CancellationToken ct = default)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        var report = editableReport!;
        // Read the line before it is gone — the audit entry names what was removed, not an id.
        var removed = report.Lines.FirstOrDefault(l => l.Id == lineId);

        var candidates = report.Lines.Where(line => line.Id == lineId || line.ParentLineId == lineId)
            .Select(line => line.Attachment).OfType<ExpenseAttachmentDto>()
            .Select(attachment => (attachment.Id, attachment.Extension)).ToList();
        IReadOnlyList<ExpenseAttachment>? removedAttachments = null;
        try
        {
            // One atomic save removes the line, its proof rows and their attachment metadata.
            removedAttachments = await repo.RemoveLineAsync(reportId, lineId, ct)
                ?? throw new InvalidOperationException("Failed to remove line.");

            await reportAudit.AuditOnBehalfEditAsync(report, actorUserId,
                removed is null
                    ? $"Removed line {lineId}"
                    : $"Removed line \"{removed.Description}\" €{removed.Amount}", CancellationToken.None);
            return ExpenseMutationResult.Success;
        }
        finally
        {
            await attachments.CleanupUnlinkedAttachmentsAsync(
                removedAttachments is null ? candidates : removedAttachments.Select(a => (a.Id, a.Extension)), lineId);
        }
    }

    private Task<(ExpenseReportDto? Report, ExpenseMutationResult? Refusal)> GetEditableReportAsync(
        Guid reportId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, CancellationToken ct) =>
        reportAccess.GetEditableReportAsync(reportId, allowedStatuses, ct);

}
