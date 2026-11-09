using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;
using Humans.Surveys.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using NodaTime;
using static Humans.Workgroups.Services.WorkgroupJournal;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupActivity(
    IWorkgroupRepository repository,
    WorkgroupAccess access,
    WorkgroupJournal journal,
    ISurveyAnalysisRead surveys,
    IClock clock) : IApplicationService
{
    internal async Task<WorkgroupMutationResult<Guid>> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateMeeting(save) is { } meetingValidationRefusal) return new(Refusal: meetingValidationRefusal);

        var now = clock.GetCurrentInstant();
        var meeting = new WorkgroupMeeting
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyMeetingFields(meeting, save);
        await repository.AddMeetingAsync(meeting, ct);

        return new(meeting.Id);
    }

    internal async Task<WorkgroupMutationResult> UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await access.RequireActiveAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateMeeting(save) is { } meetingValidationRefusal) return new(Refusal: meetingValidationRefusal);

        ApplyMeetingFields(meeting, save);
        meeting.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateMeetingAsync(meeting, ct);

        // The row is overwritten, so the audit entry is the only record of the meeting the members were told about.
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupMeetingUpdated, workgroup,
            $"Edited the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    internal async Task<WorkgroupMutationResult> DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default)
    {
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await access.RequireActiveAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var now = clock.GetCurrentInstant();
        meeting.DeletedAt = now;
        meeting.UpdatedAt = now;
        await repository.UpdateMeetingAsync(meeting, ct);

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupMeetingDeleted, workgroup,
            $"Deleted the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    internal async Task<WorkgroupMutationResult<Guid>> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(save.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);
        if (string.IsNullOrWhiteSpace(save.Body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));
        if (save.Body.Trim().Length > 16000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 16000));
        var meetingLink = await ResolveMeetingLinkAsync(workgroup.Id, save, ct);
        if (meetingLink.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);

        var now = clock.GetCurrentInstant();
        var entry = new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Kind = save.Kind,
            OccurredOn = save.OccurredOn,
            Title = Trimmed(save.Title),
            Body = save.Body.Trim(),
            MeetingId = meetingLink.Value,
            AuthorUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddLogEntryAsync(entry, ct);

        return new(entry.Id);
    }

    internal async Task<WorkgroupMutationResult> UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await access.RequireActiveAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(entry.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);
        if (RequireMemberKind(save.Kind) is { } newKindRefusal) return new(Refusal: newKindRefusal);
        if (string.IsNullOrWhiteSpace(save.Body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));
        if (save.Body.Trim().Length > 16000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 16000));
        var meetingLink = await ResolveMeetingLinkAsync(workgroup.Id, save, ct, entry.MeetingId);
        if (meetingLink.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);

        entry.Kind = save.Kind;
        entry.MeetingId = meetingLink.Value;
        entry.OccurredOn = save.OccurredOn;
        entry.Title = Trimmed(save.Title);
        entry.Body = save.Body.Trim();
        entry.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateLogEntryAsync(entry, ct);

        // The log keeps no history of its own, so without this an edit is as invisible as a deletion.
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupLogEntryUpdated, workgroup,
            $"Edited the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    internal async Task<WorkgroupMutationResult> DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default)
    {
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await access.RequireActiveAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(entry.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);

        await repository.DeleteLogEntryAsync(entryId, ct);
        // The log keeps no tombstone, so the audit trail is where the deletion stays visible.
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupLogEntryDeleted, workgroup,
            $"Deleted the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    internal async Task<WorkgroupMutationResult> LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        // The entry is a trusted record naming a survey, so it must be one the actor authored.
        var survey = (await surveys.GetSummariesAsync(ct))
            .FirstOrDefault(s => s.Id == surveyId);
        if (survey is null || survey.CreatedByUserId != actorUserId)
            return new(Refusal: new(WorkgroupErrorKeys.SurveyNotYours));

        var now = clock.GetCurrentInstant();
        await repository.AddLogEntryAsync(new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Kind = WorkgroupLogKind.SurveySubmitted,
            OccurredOn = now.InUtc().Date,
            AuthorUserId = actorUserId,
            SurveyId = surveyId,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
        return new();
    }

    private async Task<WorkgroupMutationResult<WorkgroupMeeting>> RequireLiveMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        var meeting = await repository.GetMeetingAsync(meetingId, ct);
        return meeting is { DeletedAt: null }
            ? new(meeting)
            : new(Refusal: new(WorkgroupErrorKeys.NotFound));
    }

    /// <summary>
    /// The meeting id the entry keeps, or a refusal. Only meeting notes keep a link, and a new link must name one of
    /// the group's live meetings; an unchanged link survives its meeting's deletion, so old notes stay editable.
    /// </summary>
    private async Task<WorkgroupMutationResult<Guid?>> ResolveMeetingLinkAsync(
        Guid workgroupId, WorkgroupLogEntrySave save, CancellationToken ct, Guid? currentMeetingId = null)
    {
        if (save.Kind != WorkgroupLogKind.MeetingNotes || save.MeetingId is not { } meetingId) return new();
        if (meetingId == currentMeetingId) return new(meetingId);
        var meeting = await RequireLiveMeetingAsync(meetingId, ct);
        return meeting.Value?.WorkgroupId == workgroupId ? new(meetingId) : new(Refusal: new(WorkgroupErrorKeys.NotFound));
    }

    private static WorkgroupRefusal? RequireMemberKind(WorkgroupLogKind kind)
    {
        if (kind is not (WorkgroupLogKind.Update or WorkgroupLogKind.Disclosure
            or WorkgroupLogKind.StatusRequested or WorkgroupLogKind.Note or WorkgroupLogKind.MeetingNotes))
        {
            return new(WorkgroupErrorKeys.SystemLogEntry);
        }
        return null;
    }

    private static WorkgroupRefusal? ValidateMeeting(WorkgroupMeetingSave save)
    {
        if (string.IsNullOrWhiteSpace(save.Title))
            return new(WorkgroupErrorKeys.NameRequired);
        if (save.EndUtc <= save.StartUtc)
            return new(WorkgroupErrorKeys.WindowInvalid);
        if (save.LocationUrl?.Trim().Length > 2000)
            return new(WorkgroupErrorKeys.TextTooLong, 2000);
        if (!string.IsNullOrWhiteSpace(save.LocationUrl) && WorkgroupRegisterReader.SafeHttpLink(save.LocationUrl) is null)
            return new(WorkgroupErrorKeys.LocationUrlInvalid);
        if (save.Description?.Trim().Length > 4000)
            return new(WorkgroupErrorKeys.TextTooLong, 4000);
        // The rule and its zone travel together: a rule without a zone can't say which 19:00 it repeats.
        var hasRule = !string.IsNullOrWhiteSpace(save.RecurrenceRule);
        if (hasRule != !string.IsNullOrWhiteSpace(save.RecurrenceTimezone)
            || !CalendarRecurrence.IsValidRule(save.RecurrenceRule)
            || (hasRule && (CalendarEventForm.TryResolveZone(save.RecurrenceTimezone) is null || RepeatsWithinADay(save.RecurrenceRule!))))
            return new(WorkgroupErrorKeys.RecurrenceInvalid);
        return null;
    }

    // The group page and the daily job expand a series from its start, so a sub-daily rule would
    // build an occurrence per second or minute; nobody meets that often.
    private static bool RepeatsWithinADay(string rule) =>
        rule.Split(';').Any(part =>
        {
            var equals = part.IndexOf('=');
            return equals >= 0
                && string.Equals(part[..equals].Trim(), "FREQ", StringComparison.OrdinalIgnoreCase)
                && part[(equals + 1)..].Trim().ToUpperInvariant() is "SECONDLY" or "MINUTELY" or "HOURLY";
        });

    private static void ApplyMeetingFields(WorkgroupMeeting meeting, WorkgroupMeetingSave save)
    {
        meeting.Title = save.Title.Trim();
        meeting.StartUtc = save.StartUtc;
        meeting.EndUtc = save.EndUtc;
        meeting.Description = Trimmed(save.Description);
        meeting.Location = Trimmed(save.Location);
        meeting.LocationUrl = Trimmed(save.LocationUrl);
        meeting.IsPublic = save.IsPublic;
        meeting.RecurrenceRule = Trimmed(save.RecurrenceRule);
        meeting.RecurrenceTimezone = meeting.RecurrenceRule is null ? null : save.RecurrenceTimezone!.Trim();
    }

}
