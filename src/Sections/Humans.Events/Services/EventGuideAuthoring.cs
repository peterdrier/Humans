using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Events.Services;

internal sealed class EventGuideAuthoring(
    IEventRepository repo,
    ISettingsService settingsService,
    IUserService userService,
    IEmailService emailService,
    EventsEmails emailMessages,
    IClock clock,
    ILogger<EventService> logger,
    IStringLocalizer<EventsResource> localizer) : IApplicationService
{
    internal async Task<EventSaveResult> SubmitEventAsync(Event guideEvent, string? lifecycleActionUrl = null, CancellationToken ct = default)
    {
        if (await GetOpenSubmissionSettingsAsync(ct) is not { } settings) return new("Events_SubmissionWindowClosed");
        await EnsureValidRecurrenceDaysAsync(guideEvent.IsRecurring, guideEvent.RecurrenceDays, ct);
        if (await repo.GetCategoryAsync(guideEvent.CategoryId, ct) is not { IsActive: true })
            return new("Events_CategoryUnavailable", nameof(EventEdit.CategoryId));
        if (guideEvent.CampId is null && (guideEvent.GuideSharedVenueId is not { } venueId
            || venueId == Guid.Empty || await repo.GetVenueAsync(venueId, ct) is not { IsActive: true }))
            return new("Events_VenueUnavailable", nameof(EventEdit.VenueId));
        guideEvent.EventSettingsId = settings.EventSettingsId;
        guideEvent.Submit(clock);
        await repo.AddEventAsync(guideEvent, ct);

        // A null actionUrl opts out: bulk import would otherwise send one email per CSV row.
        if (lifecycleActionUrl is not null)
            await SendLifecycleEmailAsync(guideEvent, EventStatus.Pending, reason: null, lifecycleActionUrl);
        return new();
    }

    private async Task SendLifecycleEmailAsync(
        Event guideEvent, EventStatus newStatus, string? reason, string actionUrl)
    {
        // The change is committed: request cancellation must not skip its notification.
        // Lookup and delivery failures remain best effort.
        try
        {
            var submitter = await userService.GetUserInfoAsync(guideEvent.SubmitterUserId, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(submitter?.Email))
            {
                logger.LogWarning(
                    "Skipping lifecycle email for event {EventId}: submitter {SubmitterId} has no notification email",
                    guideEvent.Id, guideEvent.SubmitterUserId);
                return;
            }

            var submitterEmail = submitter.Email;
            var language = submitter.PreferredLanguage;

            await emailService.SendAsync(emailMessages.EventLifecycle(
                submitter.Id,
                new EventLifecycleNotification(
                    NewStatus: newStatus,
                    UserName: submitter.BurnerName,
                    EventTitle: guideEvent.Title,
                    Reason: reason,
                    ActionUrl: actionUrl,
                    Culture: language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode),
                submitterEmail));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to send lifecycle email for event {EventId} (status {Status})",
                guideEvent.Id, newStatus);
        }
    }

    internal async Task<EventSaveResult> UpdateAndResubmitAsync(Guid eventId, EventEdit edit, CancellationToken ct = default)
    {
        await EnsureValidRecurrenceDaysAsync(edit.IsRecurring, edit.RecurrenceDays, ct);
        if (await repo.GetCategoryAsync(edit.CategoryId, ct) is not { IsActive: true })
            return new("Events_CategoryUnavailable", nameof(EventEdit.CategoryId));
        var guideEvent = await LoadEventAsync(eventId, ct);
        if (guideEvent.CampId is null && (edit.VenueId is not { } venueId
            || venueId == Guid.Empty || await repo.GetVenueAsync(venueId, ct) is not { IsActive: true }))
            return new("Events_VenueUnavailable", nameof(EventEdit.VenueId));
        ApplyEdit(guideEvent, edit);
        guideEvent.Resubmit(clock);
        await repo.SaveEventAsync(guideEvent, ct);
        return new();
    }

    internal async Task WithdrawEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var guideEvent = await LoadEventAsync(eventId, ct);
        guideEvent.Withdraw(clock);
        await repo.SaveEventAsync(guideEvent, ct);
    }

    internal async Task<bool> ModeratorWithdrawAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default)
    {
        var guideEvent = await LoadEventAsync(eventId, ct);
        // A moderator may withdraw whatever the submitter could.
        if (!guideEvent.CanBeWithdrawnBySubmitter)
            return false;
        guideEvent.Withdraw(clock);

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = guideEvent.Id,
            ActorUserId = actorUserId,
            Action = EventModerationActionType.Withdrawn,
            CreatedAt = guideEvent.LastUpdatedAt
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
        return true;
    }

    internal async Task AdminUpdateAsync(Guid eventId, EventEdit edit, Guid actorUserId, string? note, CancellationToken ct = default)
    {
        await EnsureValidRecurrenceDaysAsync(edit.IsRecurring, edit.RecurrenceDays, ct);
        var guideEvent = await LoadEventAsync(eventId, ct);
        ApplyEdit(guideEvent, edit);
        var now = clock.GetCurrentInstant();
        guideEvent.LastUpdatedAt = now; // Status deliberately untouched — admin edit never re-queues.

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = guideEvent.Id,
            ActorUserId = actorUserId,
            Action = EventModerationActionType.Edited,
            Reason = string.IsNullOrWhiteSpace(note) ? null : note,
            CreatedAt = now
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
    }

    /// <summary>Null when the submission window is closed or not configured.</summary>
    private async Task<EventGuideSettings?> GetOpenSubmissionSettingsAsync(CancellationToken ct)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        if (settings is not null && settings.IsSubmissionOpenAt(clock.GetCurrentInstant())) return settings;
        logger.LogWarning("Event submission refused: the submission window is closed or not configured");
        return null;
    }

    private async Task EnsureValidRecurrenceDaysAsync(bool isRecurring, string? recurrenceDays, CancellationToken ct)
    {
        if (!isRecurring) return;
        var settings = await repo.GetGuideSettingsAsync(ct);
        var eventSettings = settings is null ? null : await settingsService.GetEventSettingsByIdAsync(settings.EventSettingsId, ct);
        if (eventSettings is null || !EventRecurrenceDays.HasOnlyEventOffsets(recurrenceDays, eventSettings.EventEndOffset))
            throw new InvalidOperationException("Recurrence days must be day offsets inside the event calendar.");
    }

    private async Task<Event> LoadEventAsync(Guid eventId, CancellationToken ct) =>
        await repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

    // The event's own CampId, not the caller, decides which field set applies.
    private static void ApplyEdit(Event guideEvent, EventEdit edit)
    {
        if (guideEvent.CampId.HasValue)
        {
            guideEvent.ApplyBarrioEdit(
                edit.CategoryId, edit.Title, edit.Description, edit.StartAt, edit.DurationMinutes,
                edit.LocationNote, edit.Host, edit.IsRecurring, edit.RecurrenceDays, edit.PriorityRank);
        }
        else
        {
            guideEvent.ApplyIndividualEdit(
                edit.CategoryId,
                edit.VenueId is { } venueId && venueId != Guid.Empty
                    ? venueId
                    : throw new InvalidOperationException($"Individual event {guideEvent.Id} edit requires a venue."),
                edit.Title, edit.Description, edit.StartAt, edit.DurationMinutes,
                edit.LocationNote, edit.Host, edit.IsRecurring, edit.RecurrenceDays);
        }
    }

    internal async Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default)
    {
        if (await GetOpenSubmissionSettingsAsync(ct) is not { } settings)
            return new BulkImportResult([], 0, 0, RefusalKey: "Events_SubmissionWindowClosed");
        return await new EventBulkImporter(repo, clock, localizer).ImportAsync(
            campId, submitterUserId, settings.EventSettingsId, rows, gateOpeningDate, eventEndOffset, timeZone, ct);
    }

    internal async Task<ModerationOutcome> ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason,
        string? submitterEditUrl = null, CancellationToken ct = default)
    {
        if (actionType is EventModerationActionType.Rejected or EventModerationActionType.ResubmitRequested
            && string.IsNullOrWhiteSpace(reason))
            return ModerationOutcome.ReasonRequired;

        var guideEvent = await repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

        if (guideEvent.Status is not EventStatus.Pending)
            return ModerationOutcome.NotPending;

        guideEvent.ApplyModerationAction(actionType, clock);

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = eventId,
            ActorUserId = actorUserId,
            Action = actionType,
            Reason = reason,
            CreatedAt = clock.GetCurrentInstant()
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);

        if (submitterEditUrl is not null)
        {
            var lifecycleStatus = actionType switch
            {
                EventModerationActionType.Approved => (EventStatus?)EventStatus.Approved,
                EventModerationActionType.Rejected => EventStatus.Rejected,
                EventModerationActionType.ResubmitRequested => EventStatus.ResubmitRequested,
                _ => null
            };
            if (lifecycleStatus.HasValue)
                await SendLifecycleEmailAsync(guideEvent, lifecycleStatus.Value, reason, submitterEditUrl);
        }

        return ModerationOutcome.Applied;
    }

}
