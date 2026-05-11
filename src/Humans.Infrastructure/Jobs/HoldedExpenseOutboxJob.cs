using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Helpers;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedExpenseOutboxJob : IRecurringJob
{
    private const int BatchSize = 100;

    private readonly IExpenseRepository _expenseRepository;
    private readonly IBudgetService _budgetService;
    private readonly IUserService _userService;
    private readonly IHoldedClient _holdedClient;
    private readonly IExpenseAttachmentStorageService _attachmentStorage;
    private readonly IClock _clock;
    private readonly ILogger<HoldedExpenseOutboxJob> _logger;

    public HoldedExpenseOutboxJob(
        IExpenseRepository expenseRepository,
        IBudgetService budgetService,
        IUserService userService,
        IHoldedClient holdedClient,
        IExpenseAttachmentStorageService attachmentStorage,
        IClock clock,
        ILogger<HoldedExpenseOutboxJob> logger)
    {
        _expenseRepository = expenseRepository;
        _budgetService = budgetService;
        _userService = userService;
        _holdedClient = holdedClient;
        _attachmentStorage = attachmentStorage;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var events = await _expenseRepository
            .GetUnprocessedOutboxAsync(BatchSize, cancellationToken);

        if (events.Count == 0)
        {
            return;
        }

        foreach (var outboxEvent in events)
        {
            try
            {
                var report = await _expenseRepository
                    .GetByIdAsync(outboxEvent.ExpenseReportId, cancellationToken);

                if (report is null)
                {
                    _logger.LogWarning(
                        "Outbox event {OutboxEventId} references missing report {ReportId} — marking permanently failed",
                        outboxEvent.Id, outboxEvent.ExpenseReportId);
                    await _expenseRepository.MarkOutboxFailedPermanentlyAsync(
                        outboxEvent.Id,
                        "Report not found",
                        _clock.GetCurrentInstant(),
                        cancellationToken);
                    continue;
                }

                var category = await _budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
                var tag = BuildTag(category?.BudgetGroup?.Name, category?.Name);

                var users = await _userService.GetByIdsAsync(
                    [report.SubmitterUserId], cancellationToken);
                var submitterName = users.TryGetValue(report.SubmitterUserId, out var user)
                    ? user.DisplayName
                    : "Unknown";

                var now = _clock.GetCurrentInstant();

                switch (outboxEvent.EventType)
                {
                    case HoldedExpenseOutboxEventType.CreateIncomingDoc:
                        await ProcessCreateAsync(
                            outboxEvent.Id, report, tag, submitterName, now, cancellationToken);
                        break;

                    case HoldedExpenseOutboxEventType.UpdateIncomingDocTag:
                        await ProcessUpdateTagAsync(
                            outboxEvent.Id, report, tag, now, cancellationToken);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown outbox event type '{outboxEvent.EventType}'.");
                }
            }
            catch (HoldedTransientException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Transient error processing Holded outbox event {OutboxEventId} — will retry",
                    outboxEvent.Id);
                await _expenseRepository.IncrementOutboxRetryAsync(
                    outboxEvent.Id, ex.Message, cancellationToken);
            }
            catch (HoldedPermanentException ex)
            {
                _logger.LogError(
                    ex,
                    "Permanent error processing Holded outbox event {OutboxEventId} — HTTP {StatusCode}",
                    outboxEvent.Id, ex.StatusCode);
                await _expenseRepository.MarkOutboxFailedPermanentlyAsync(
                    outboxEvent.Id, ex.Message, _clock.GetCurrentInstant(), cancellationToken);
            }
        }
    }

    private async Task ProcessCreateAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string tag,
        string submitterName,
        Instant now,
        CancellationToken ct)
    {
        var input = new HoldedPurchaseDocumentInput
        {
            ContactName = submitterName,
            Date = report.SubmittedAt ?? report.CreatedAt,
            Description = report.Note ?? "",
            Tags = [tag],
            Lines = report.Lines
                .OrderBy(l => l.SortOrder)
                .Select(l => new HoldedPurchaseDocumentLineInput
                {
                    Description = l.Description,
                    Amount = l.Amount,
                    Tags = [tag],
                })
                .ToList(),
        };

        var holdedDocId = await _holdedClient.CreatePurchaseDocumentAsync(input, ct);

        foreach (var line in report.Lines.OrderBy(l => l.SortOrder))
        {
            if (line.AttachmentId is null || line.Attachment is null)
            {
                continue;
            }

            var stream = await _attachmentStorage.OpenReadAsync(
                line.Attachment.Id, line.Attachment.Extension, ct);
            try
            {
                await _holdedClient.UploadAttachmentAsync(
                    holdedDocId,
                    new HoldedAttachmentInput
                    {
                        FileName = line.Attachment.OriginalFileName,
                        ContentType = line.Attachment.ContentType,
                        Content = stream,
                    },
                    ct);
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }

        await _expenseRepository.SetHoldedDocIdAsync(
            report.Id, holdedDocId, outboxEventId, now, ct);
    }

    private async Task ProcessUpdateTagAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string tag,
        Instant now,
        CancellationToken ct)
    {
        await _holdedClient.UpdatePurchaseDocumentTagsAsync(
            report.HoldedDocId!,
            [tag],
            ct);

        await _expenseRepository.MarkOutboxProcessedAsync(outboxEventId, now, ct);
    }

    private static string BuildTag(string? groupName, string? categoryName)
    {
        var groupSlug = string.IsNullOrWhiteSpace(groupName)
            ? "unknown"
            : SlugHelper.GenerateSlug(groupName);
        var categorySlug = string.IsNullOrWhiteSpace(categoryName)
            ? "unknown"
            : SlugHelper.GenerateSlug(categoryName);
        return $"{groupSlug}-{categorySlug}";
    }
}
