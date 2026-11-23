using System.Text.RegularExpressions;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Google.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Surveys.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;
using System.Globalization;
using Humans.Gdpr.Contracts;
using Humans.Workgroups.Services.Contributors;

namespace Humans.Workgroups.Services;

internal sealed partial class WorkgroupService(
    IWorkgroupRepository repository,
    IUserService users,
    ISurveyAnalysisRead surveys,
    IRoleAssignmentService roles,
    ISettingsService settings,
    IGoogleSyncService googleSync,
    IGoogleSyncWrite googleSyncWrite,
    IGoogleGroupSync googleGroupSync,
    IFinanceWrite finance,
    IHoldedService holded,
    INotificationService notifications,
    INotificationEmitter notificationEmitter,
    IEmailService email,
    WorkgroupsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<WorkgroupService> logger) : IWorkgroupService
{
    private static readonly string[] ReservedSlugs = ["apply", "admin"];

    public async Task<WorkgroupMutationResult<Guid>> ApplyAsync(
        Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default)
    {
        ct = CancellationToken.None;
        ArgumentNullException.ThrowIfNull(application);
        var validation = await ValidateApplicationAsync(application, ct);
        if (validation.Refusal is { } applicationRefusal) return new(Refusal: applicationRefusal);
        application = validation.Value!;

        var now = clock.GetCurrentInstant();
        var slugResult = await ReserveSlugAsync(application.Name, null, ct);
        if (slugResult.Refusal is { } slugRefusal) return new(Refusal: slugRefusal);
        var slug = slugResult.Value!;

        var workgroup = new Workgroup
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Status = WorkgroupStatus.Applied,
            AppliedByUserId = actorUserId,
            AppliedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyFields(workgroup, application.Name, application.Purpose, application.Deliverable,
            application.DeliverableKind, application.Audience, application.TargetDate,
            application.DiscordChannelUrl, application.IsSensitive);

        workgroup.Members.Add(NewMember(workgroup.Id, actorUserId, WorkgroupMemberRole.Coordinator, now));
        if (application.SecondCoordinatorUserId is { } second && second != actorUserId)
            workgroup.Members.Add(NewMember(workgroup.Id, second, WorkgroupMemberRole.Coordinator, now));

        workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.Applied, now,
            body: null));

        await repository.AddWorkgroupAsync(workgroup, ct);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
            $"Working group applied: {workgroup.Name}", info,
            "A new working group is waiting on the Secretary.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Applied, info, detail: null, ct);

        return new(workgroup.Id);
    }

    public async Task<WorkgroupMutationResult> RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var now = clock.GetCurrentInstant();
        var info = ToInfo(workgroup);

        await repository.AddLogEntryAsync(new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Kind = WorkgroupLogKind.StatusRequested,
            OccurredOn = now.InUtc().Date,
            Body = Trimmed(question),
            AuthorUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
        ct = CancellationToken.None;

        await NotifyAsync(info.CoordinatorUserIds(), WorkgroupsNotificationSources.WorkgroupReportingDue,
            "Workgroups_Todo_StatusRequested_Title", info, Trimmed(question), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateRegisterFields(edit.Name, edit.Purpose, edit.Deliverable, edit.DiscordChannelUrl) is { } fieldsRefusal) return new(Refusal: fieldsRefusal);

        var now = clock.GetCurrentInstant();
        // The deliverable sentence is the group's promise: changing any part of it is a scope change on the record.
        var scopeChanged = !string.Equals(workgroup.Deliverable, edit.Deliverable.Trim(), StringComparison.Ordinal)
            || workgroup.DeliverableKind != edit.DeliverableKind
            || workgroup.Audience != edit.Audience
            || workgroup.TargetDate != edit.TargetDate;

        if (!string.Equals(workgroup.Name, edit.Name.Trim(), StringComparison.Ordinal))
        {
            var slugResult = await ReserveSlugAsync(edit.Name, workgroup.Id, ct);
            if (slugResult.Refusal is { } slugRefusal) return new(Refusal: slugRefusal);
            workgroup.Slug = slugResult.Value!;
        }

        ApplyFields(workgroup, edit.Name, edit.Purpose, edit.Deliverable, edit.DeliverableKind,
            edit.Audience, edit.TargetDate, edit.DiscordChannelUrl, edit.IsSensitive);
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct, scopeChanged
            ? [SystemEntry(workgroup.Id, WorkgroupLogKind.ScopeChanged, now,
                $"By {workgroup.TargetDate?.ToInvariantLongDate() ?? "no date yet"} we will deliver "
                    + $"{workgroup.Deliverable} to the {workgroup.Audience}.", authorUserId: actorUserId)]
            : null);
        return new();
    }

    public async Task<WorkgroupMutationResult> MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default)
    {
        // Quiet is the Secretary's close, never a member's own ending.
        if (reason is not (WorkgroupDormantReason.Delivered or WorkgroupDormantReason.Abandoned))
            return new(Refusal: new(WorkgroupErrorKeys.DoneReasonInvalid));

        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        // Ending while a comment period runs would cut short a window the group promised publicly.
        var now = clock.GetCurrentInstant();
        if (workgroup.Documents.Any(d => d.CommentsCloseAt is { } closes && closes > now))
            return new(Refusal: new(WorkgroupErrorKeys.CommentsStillOpen));

        await EndAsync(workgroup, actorUserId, reason, reasons: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> SetGoogleGroupAsync(
        Guid workgroupId, Guid actorUserId, string? prefix, CancellationToken ct = default)
    {
        var parsed = ParseGoogleGroupPrefix(prefix);
        if (parsed.Refusal is { } prefixRefusal) return new(Refusal: prefixRefusal);
        var newPrefix = parsed.Value;

        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        if (string.Equals(workgroup.GoogleGroupPrefix, newPrefix, StringComparison.Ordinal))
            return new();

        // Two groups on one address would fight over its membership in the fan-out.
        if (newPrefix is not null && (await GetRegisterAsync(ct)).Any(w => w.Id != workgroup.Id
                && string.Equals(w.GoogleGroupEmail, $"{newPrefix}@{DomainConstants.GoogleGroupDomain}", StringComparison.OrdinalIgnoreCase)))
            return new(Refusal: new("Another workgroup already uses this email group.") { IsOperatorMessage = true });

        workgroup.GoogleGroupPrefix = newPrefix;
        workgroup.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        ct = CancellationToken.None;

        await AuditAsync(WorkgroupsAuditActions.WorkgroupGoogleGroupSet, workgroup,
            workgroup.GoogleGroupEmail is { } groupEmail ? $"Email group set to {groupEmail}" : "Email group cleared", actorUserId);
        await RequestGoogleSyncAsync(workgroup, ct);
        return new();
    }

    /// <summary>Same rule as a team's group prefix. Admins paste the full address as often as its local part; only our own domain is accepted.</summary>
    private static WorkgroupMutationResult<string?> ParseGoogleGroupPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new((string?)null);

        var prefix = value.Trim().ToLowerInvariant();
        var at = prefix.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            if (!string.Equals(prefix[(at + 1)..], DomainConstants.GoogleGroupDomain, StringComparison.Ordinal))
                return new(Refusal: new($"The email group must be on {DomainConstants.GoogleGroupDomain}.") { IsOperatorMessage = true });
            prefix = prefix[..at];
        }

        return prefix.Length <= 64 && GoogleGroupPrefixPattern.IsMatch(prefix)
            ? new(prefix)
            : new(Refusal: new("Use only lowercase letters, numbers and hyphens for the email group name.") { IsOperatorMessage = true });
    }

    private static readonly Regex GoogleGroupPrefixPattern =
        new("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    internal async Task<WorkgroupMutationResult<Workgroup>> RequireAsync(Guid workgroupId, CancellationToken ct)
    {
        var workgroup = await repository.GetWorkgroupAsync(workgroupId, ct);
        return workgroup is null
            ? new(Refusal: new(WorkgroupErrorKeys.NotFound))
            : new(workgroup);
    }

    /// <summary>The group, refused unless it exists and accepts member work.</summary>
    internal async Task<WorkgroupMutationResult<Workgroup>> RequireActiveAsync(Guid workgroupId, CancellationToken ct)
    {
        var result = await RequireAsync(workgroupId, ct);
        if (result.Refusal is null && RequireAcceptsMemberWork(result.Value!) is { } stateRefusal)
            return new(Refusal: stateRefusal);
        return result;
    }

    /// <summary>Member mutations are frozen before registration and once the group ends.</summary>
    internal static WorkgroupRefusal? RequireAcceptsMemberWork(Workgroup w)
    {
        if (w.Status != WorkgroupStatus.Active)
            return new(WorkgroupErrorKeys.Frozen);
        return null;
    }

    internal static WorkgroupRefusal? RequireStatus(Workgroup w, params WorkgroupStatus[] allowed)
    {
        if (!allowed.Contains(w.Status))
            return new(WorkgroupErrorKeys.WrongStatus);
        return null;
    }

    internal async Task<Guid?> ResolveMemberTargetAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        return user is null || user.IsTombstone ? null : user.Id;
    }

    public async Task<WorkgroupMutationResult<Guid>> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
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

    public async Task<WorkgroupMutationResult> UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await RequireActiveAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateMeeting(save) is { } meetingValidationRefusal) return new(Refusal: meetingValidationRefusal);

        ApplyMeetingFields(meeting, save);
        meeting.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateMeetingAsync(meeting, ct);

        // The row is overwritten, so the audit entry is the only record of the meeting the members were told about.
        await AuditAsync(WorkgroupsAuditActions.WorkgroupMeetingUpdated, workgroup,
            $"Edited the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default)
    {
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await RequireActiveAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var now = clock.GetCurrentInstant();
        meeting.DeletedAt = now;
        meeting.UpdatedAt = now;
        await repository.UpdateMeetingAsync(meeting, ct);

        await AuditAsync(WorkgroupsAuditActions.WorkgroupMeetingDeleted, workgroup,
            $"Deleted the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
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

    public async Task<WorkgroupMutationResult> UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireActiveAsync(entry.WorkgroupId, ct);
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
        await AuditAsync(WorkgroupsAuditActions.WorkgroupLogEntryUpdated, workgroup,
            $"Edited the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default)
    {
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireActiveAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(entry.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);

        await repository.DeleteLogEntryAsync(entryId, ct);
        // The log keeps no tombstone, so the audit trail is where the deletion stays visible.
        await AuditAsync(WorkgroupsAuditActions.WorkgroupLogEntryDeleted, workgroup,
            $"Deleted the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    public async Task<WorkgroupMutationResult> LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
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
        if (!string.IsNullOrWhiteSpace(save.LocationUrl) && SafeHttpLink(save.LocationUrl) is null)
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

    private const decimal MaxBudgetAmount = 9_999_999_999_999_999.99m;

    public async Task<WorkgroupMutationResult<ExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (ValidateBudgetAmount(save.Amount) is { } budgetRefusal)
            return new(Refusal: budgetRefusal);

        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        // Finance first, before any write: a failed create or unknown account leaves the row as it was.
        ExpenseAccountRef? account = null;
        var needsAccount = save.Amount is not null
            && (workgroup.HoldedAccountNumber is null
                || (save.ExistingAccountNum is { } wanted && wanted != workgroup.HoldedAccountNumber));
        if (needsAccount)
        {
            // Account resolution can create remote state; finish its local binding and record.
            ct = CancellationToken.None;
            var accountResult = await ResolveBudgetAccountAsync(workgroup, save.ExistingAccountNum, ct);
            if (accountResult.Refusal is { } accountRefusal) return new(Refusal: accountRefusal);
            account = accountResult.Value!;
        }

        var now = clock.GetCurrentInstant();
        workgroup.BudgetAmount = save.Amount;
        if (account is not null)
        {
            workgroup.HoldedAccountNumber = account.AccountNum;
            workgroup.HoldedAccountId = account.AccountId;
        }
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.BudgetSet, now, BudgetLogBody(workgroup),
                authorUserId: actorUserId)]);
        await AuditAsync(WorkgroupsAuditActions.WorkgroupBudgetSet, workgroup, BudgetAuditSummary(workgroup), actorUserId);
        return new(account);
    }

    public async Task<IReadOnlyList<HoldedChartAccountInfo>> ListExpenseAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            return (await holded.GetChartAsync(ct))
                .Where(a => a.Number is >= 60000000 and <= 69999999 && !a.Archived)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Holded mirror unavailable; expense accounts could not be loaded");
            throw;
        }
    }

    /// <summary>Finance faults log and return retry guidance; cancellation propagates.</summary>
    internal async Task<WorkgroupMutationResult<ExpenseAccountRef>> ResolveBudgetAccountAsync(
        Workgroup workgroup, int? existingAccountNum, CancellationToken ct)
    {
        try
        {
            return new(await finance.CreateOrLinkExpenseAccountAsync(
                $"Workgroups / {workgroup.Name}", existingAccountNum, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Finance could not resolve a Holded account for workgroup {WorkgroupId}", workgroup.Id);
            return new(Refusal: new(WorkgroupErrorKeys.BudgetAccountFailed));
        }
    }

    internal static WorkgroupRefusal? ValidateBudgetAmount(decimal? amount)
    {
        if (amount is < 0) return new(WorkgroupErrorKeys.BudgetNegative);
        if (amount is > MaxBudgetAmount)
            return new("The budget amount is too large.") { IsOperatorMessage = true };
        if (amount is { } value && decimal.Round(value, 2) != value)
            return new("Budget amounts must have no more than two decimal places.") { IsOperatorMessage = true };
        return null;
    }

    // Read in all supported cultures under the localized "Budget set" badge, so it carries figures only, never English prose.
    internal static string BudgetLogBody(Workgroup w) => w.BudgetAmount is { } amount
        ? $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR · {w.HoldedAccountNumber}"
        : "—";

    internal static string BudgetAuditSummary(Workgroup w) => w.BudgetAmount is { } amount
        ? $"Budget set to {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR, account {w.HoldedAccountNumber}"
        : "Budget cleared";

    /// <summary>
    /// Failures are logged, never surfaced: the lifecycle step is the Board's decision and doesn't wait on Holded.
    /// </summary>
    internal async Task SetAccountActiveAsync(Workgroup w, bool isActive, CancellationToken ct)
    {
        if (w.HoldedAccountNumber is not { } accountNum) return;
        try
        {
            await finance.SetExpenseAccountActiveAsync(accountNum, isActive, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not set Holded account {AccountNum} active={IsActive} for workgroup {WorkgroupId}",
                accountNum, isActive, w.Id);
        }
    }

    internal const string WorkgroupApplications = "WorkgroupApplications";
    internal const string WorkgroupMemberships = "WorkgroupMemberships";
    internal const string WorkgroupLogEntries = "WorkgroupLogEntries";
    internal const string WorkgroupMeetings = "WorkgroupMeetings";
    internal const string WorkgroupDocuments = "WorkgroupDocuments";
    internal const string WorkgroupComments = "WorkgroupComments";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await repository.GetRowsForUserAsync(userId, ct);

        // Group names come from the register, so no row type carries a denormalized copy.
        var graph = await repository.GetGraphAsync(ct);
        var names = graph.Workgroups.ToDictionary(w => w.Id, w => w.Name);
        var documentGroups = graph.Workgroups
            .SelectMany(w => w.Documents.Select(d => (d.Id, d.Title, GroupId: w.Id)))
            .ToDictionary(x => x.Id, x => (x.Title, x.GroupId));

        string NameOf(Guid workgroupId) => names.TryGetValue(workgroupId, out var name) ? name : "(unknown)";

        return
        [
            // Erasure nulls the applicant attribution on the group row, so it is disclosed here too.
            new UserDataSlice(WorkgroupApplications, rows.AppliedFor
                .Select(w => new
                {
                    Workgroup = w.Name,
                    Status = w.Status.ToString(),
                    w.Purpose,
                    AppliedAt = w.AppliedAt.ToIso8601(),
                    RegisteredAt = w.RegisteredAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupMemberships, rows.Memberships
                .Select(m => new
                {
                    Workgroup = NameOf(m.WorkgroupId),
                    Role = m.Role.ToString(),
                    Pending = m.IsPending == true,
                    JoinedAt = m.JoinedAt.ToIso8601(),
                    LeftAt = m.LeftAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupLogEntries, rows.LogEntries
                .Select(e => new
                {
                    Workgroup = NameOf(e.WorkgroupId),
                    Kind = e.Kind.ToString(),
                    OccurredOn = e.OccurredOn.ToInvariantDate(),
                    e.Title,
                    e.Body,
                    CreatedAt = e.CreatedAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupMeetings, rows.Meetings
                .Select(m => new
                {
                    Workgroup = NameOf(m.WorkgroupId),
                    m.Title,
                    m.Description,
                    StartUtc = m.StartUtc.ToIso8601(),
                    EndUtc = m.EndUtc.ToIso8601(),
                    m.RecurrenceRule,
                    m.RecurrenceTimezone,
                    m.Location,
                    m.IsPublic,
                    m.Minutes,
                    CreatedAt = m.CreatedAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupDocuments, rows.Documents
                .Select(d => new
                {
                    Workgroup = NameOf(d.WorkgroupId),
                    d.Title,
                    Kind = d.Kind.ToString(),
                    Status = d.Status.ToString(),
                    Authored = d.CreatedByUserId == userId,
                    Edited = d.UpdatedByUserId == userId,
                    DispositionRecorded = d.DispositionByUserId == userId,
                    CreatedAt = d.CreatedAt.ToIso8601(),
                    UpdatedAt = d.UpdatedAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupComments, rows.Comments
                .Select(c => new
                {
                    Workgroup = documentGroups.TryGetValue(c.DocumentId, out var doc)
                        ? NameOf(doc.GroupId)
                        : "(unknown)",
                    Document = doc.Title ?? "(unknown)",
                    c.Category,
                    c.Body,
                    // They may appear as responder or moderator on somebody else's comment.
                    Authored = c.AuthorUserId == userId,
                    Responded = c.RespondedByUserId == userId,
                    HiddenByThisPerson = c.HiddenByUserId == userId,
                    Disposition = c.Disposition.ToString(),
                    c.Response,
                    Hidden = c.HiddenAt is not null,
                    c.HiddenReason,
                    CreatedAt = c.CreatedAt.ToIso8601()
                })
                .ToList())
        ];
    }

    /// <summary>Matches <c>WorkgroupDocumentComment.Category</c>, which every comment in an opened window must fit.</summary>
    private const int MaxCommentCategoryLength = 100;

    public async Task<WorkgroupMutationResult<Guid>> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (string.IsNullOrWhiteSpace(save.Title))
            return new(Refusal: new(WorkgroupErrorKeys.NameRequired));

        var now = clock.GetCurrentInstant();
        var document = new WorkgroupDocument
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Title = save.Title.Trim(),
            Kind = save.Kind,
            Body = save.Body ?? string.Empty,
            Status = WorkgroupDocumentStatus.Draft,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddDocumentAsync(document, ct);
        return new(document.Id);
    }

    public async Task<WorkgroupMutationResult> UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (_, document) = documentResult.Value;
        if (string.IsNullOrWhiteSpace(save.Title))
            return new(Refusal: new(WorkgroupErrorKeys.NameRequired));
        // Delivered is the association's copy of what it received; the body stops moving.
        if (document.Status == WorkgroupDocumentStatus.Delivered)
            return new(Refusal: new(WorkgroupErrorKeys.DocumentFrozen));

        document.Title = save.Title.Trim();
        document.Kind = save.Kind;
        document.Body = save.Body ?? string.Empty;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateDocumentAsync(document, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (document.Status != WorkgroupDocumentStatus.Draft)
            return new(Refusal: new(WorkgroupErrorKeys.WrongStatus));
        if (string.IsNullOrWhiteSpace(document.Body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));

        var now = clock.GetCurrentInstant();
        document.Status = WorkgroupDocumentStatus.Published;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.DocumentPublished, now,
                document.Title, authorUserId: actorUserId, documentId: document.Id)]);
        ct = CancellationToken.None;

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            "Enum_WorkgroupLogKind_DocumentPublished", info, document.Title, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (document.Status != WorkgroupDocumentStatus.Published)
            return new(Refusal: new(WorkgroupErrorKeys.NotPublished));

        var categories = window.Categories
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (categories.Count == 0)
            return new(Refusal: new(WorkgroupErrorKeys.CategoriesRequired));
        // A longer category would open a window whose own comments could never be saved.
        if (categories.Any(c => c.Length > MaxCommentCategoryLength))
            return new(Refusal: new(WorkgroupErrorKeys.CategoryTooLong));
        if (window.ClosesAt <= window.OpensAt)
            return new(Refusal: new(WorkgroupErrorKeys.WindowInvalid));

        var now = clock.GetCurrentInstant();
        document.CommentCategories = categories;
        document.CommentsOpenAt = window.OpensAt;
        document.CommentsCloseAt = window.ClosesAt;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.CommentPeriodOpened, now,
                document.Title, authorUserId: actorUserId, documentId: document.Id)]);
        ct = CancellationToken.None;

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            "Enum_WorkgroupLogKind_CommentPeriodOpened", info, document.Title, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (document.CommentsOpenAt is null)
            return new(Refusal: new(WorkgroupErrorKeys.WrongStatus));

        var now = clock.GetCurrentInstant();
        document.CommentsCloseAt = now;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.CommentPeriodClosed, now,
                document.Title, authorUserId: actorUserId, documentId: document.Id)]);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (document.Status != WorkgroupDocumentStatus.Published)
            return new(Refusal: new(WorkgroupErrorKeys.NotPublished));

        var now = clock.GetCurrentInstant();
        // Delivering mid-window would cut short a publicly promised comment period.
        if (document.CommentsCloseAt is { } closes && closes > now)
            return new(Refusal: new(WorkgroupErrorKeys.CommentsStillOpen));

        document.Status = WorkgroupDocumentStatus.Delivered;
        document.DeliveredAt = now;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.Delivered, now,
                document.Title, authorUserId: actorUserId, documentId: document.Id)]);
        ct = CancellationToken.None;

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            $"Delivered for a decision: {document.Title}", info,
            $"{workgroup.Name} delivered {document.Title} to the {workgroup.Audience}.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Delivered, info, document.Title, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        var now = clock.GetCurrentInstant();

        if (workgroup.IsSensitive == true && !workgroup.Members.Any(m => m.UserId == actorUserId && m.LeftAt is null && m.IsPending != true))
            return new(Refusal: new(WorkgroupErrorKeys.MembersOnly));

        // Beyond that the window is the only gate: membership doesn't matter.
        if (!ToInfo(document).IsOpenForComment(now))
            return new(Refusal: new(WorkgroupErrorKeys.CommentsClosed));

        var matched = document.CommentCategories
            .FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null) return new(Refusal: new(WorkgroupErrorKeys.UnknownCategory));
        if (string.IsNullOrWhiteSpace(body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));
        if (body.Trim().Length > 4000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 4000));

        var comment = new WorkgroupDocumentComment
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Category = matched,
            AuthorUserId = actorUserId,
            Body = body.Trim(),
            Disposition = WorkgroupCommentDisposition.Pending,
            CreatedAt = now
        };
        await repository.AddCommentAsync(comment, ct);
        return new(comment.Id);
    }

    public async Task<WorkgroupMutationResult> RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        var comment = await repository.GetCommentAsync(commentId, ct);
        if (comment is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        // Responding stays open after the window closes, but not after the group ends.
        var workgroupResult = await RequireActiveAsync(comment.Document.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        if (response?.Trim().Length is > 4000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 4000));

        var now = clock.GetCurrentInstant();
        Respond(comment, disposition, response, actorUserId, now);
        await repository.UpdateCommentsAsync([comment], ct);
        ct = CancellationToken.None;

        if (comment.AuthorUserId is { } author)
        {
            var info = ToInfo(workgroup);
            await NotifyAsync([author], WorkgroupsNotificationSources.WorkgroupDocumentActivity,
                "Workgroups_CommentAnswered", info, $"{comment.Document.Title}: {Trimmed(response)}", ct);
        }
        return new();
    }

    public async Task<WorkgroupMutationResult> RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;

        var matched = document.CommentCategories
            .FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null) return new(Refusal: new(WorkgroupErrorKeys.UnknownCategory));

        // Only still-Pending comments: one answered individually keeps its own answer.
        var pending = document.Comments
            .Where(c => c.Disposition == WorkgroupCommentDisposition.Pending
                && c.HiddenAt is null
                && string.Equals(c.Category, matched, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pending.Count == 0)
            return new();
        if (response?.Trim().Length is > 4000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 4000));

        var now = clock.GetCurrentInstant();
        foreach (var comment in pending)
            Respond(comment, disposition, response, actorUserId, now);
        await repository.UpdateCommentsAsync(pending, ct);
        ct = CancellationToken.None;

        var info = ToInfo(workgroup);
        var authors = pending.Select(c => c.AuthorUserId).OfType<Guid>().Distinct().ToList();
        await NotifyAsync(authors, WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            "Workgroups_CommentAnswered", info, $"{document.Title}: {Trimmed(response)}", ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> HideCommentAsync(
        Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new(Refusal: new(WorkgroupErrorKeys.HideReasonRequired));

        var comment = await repository.GetCommentAsync(commentId, ct);
        if (comment is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireActiveAsync(comment.Document.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        if (reason.Trim().Length > 500)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 500));

        comment.HiddenAt = clock.GetCurrentInstant();
        comment.HiddenByUserId = actorUserId;
        comment.HiddenReason = reason.Trim();
        await repository.UpdateCommentsAsync([comment], ct);

        // Audited so the Board can review moderation; the row shows only "hidden by the group".
        await AuditAsync(WorkgroupsAuditActions.WorkgroupCommentHidden, workgroup,
            $"Hid a comment on {comment.Document.Title}: {reason.Trim()}", actorUserId,
            AuditEntityTypes.WorkgroupComment, comment.Id);
        return new();
    }

    private static void Respond(
        WorkgroupDocumentComment comment,
        WorkgroupCommentDisposition disposition,
        string? response,
        Guid actorUserId,
        NodaTime.Instant now)
    {
        comment.Disposition = disposition;
        comment.Response = Trimmed(response);
        comment.RespondedByUserId = actorUserId;
        comment.RespondedAt = now;
    }

    internal async Task<WorkgroupMutationResult<(Workgroup Workgroup, WorkgroupDocument Document)>> RequireDocumentAsync(
        Guid documentId, CancellationToken ct, bool requireActive = true)
    {
        var document = await repository.GetDocumentAsync(documentId, ct);
        if (document is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroup = requireActive
            ? await RequireActiveAsync(document.WorkgroupId, ct)
            : await RequireAsync(document.WorkgroupId, ct);
        return workgroup.Refusal is { } refusal
            ? new(Refusal: refusal)
            : new((workgroup.Value!, document));
    }

    public async Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default)
    {
        var stored = await repository.GetRootDriveFolderIdAsync(ct)
            // Legacy Settings value, used until an admin saves a local one.
            ?? await settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, ct);
        // Values saved before the setter normalized may still hold the pasted URL.
        return stored is null ? null : NormalizeFolderId(stored);
    }

    public async Task<WorkgroupMutationResult> SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        var id = NormalizeFolderId(folderId);
        // A missing root folder blocks every registration, so refuse it now rather than then.
        if (!await googleSync.IsFolderReachableAsync(id, ct))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderUnreachable));

        await repository.SetRootDriveFolderIdAsync(id, ct);
        await auditLog.LogAsync(WorkgroupsAuditActions.WorkgroupsRootFolderUpdated,
            AuditEntityTypes.WorkgroupsSettings, Guid.Empty,
            $"Root Drive folder set to {id}", actorUserId);
        return new();
    }

    /// <summary>Secretaries paste the folder's browser URL as often as its id; only the id is kept.</summary>
    private static string NormalizeFolderId(string value) =>
        DriveFolderUrlId.Match(value) is { Success: true } m ? m.Groups["id"].Value : value.Trim();

    private static readonly Regex DriveFolderUrlId = new(@"(?:/folders/|[?&]id=)(?<id>[A-Za-z0-9_-]+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
    /// <summary>Neither failure (unset root, Google error) leaves the group half-registered.</summary>
    internal async Task<WorkgroupMutationResult<string>> CreateGroupFolderAsync(Workgroup workgroup, CancellationToken ct)
    {
        var root = await GetRootDriveFolderIdAsync(ct);
        if (string.IsNullOrWhiteSpace(root))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        try
        {
            return new(await googleSyncWrite.CreateSubfolderAsync(root, workgroup.Name, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create the Drive subfolder for workgroup {WorkgroupId}",
                workgroup.Id);
            return new(Refusal: new(WorkgroupErrorKeys.DriveFolderCreationFailed));
        }
    }

    /// <summary>Failures are logged, never surfaced: the daily reconciliation covers the drift.</summary>
    internal async Task RequestGoogleSyncAsync(Workgroup w, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(w.DriveFolderId))
        {
            try
            {
                await googleSyncWrite.RequestSyncAsync(w.DriveFolderId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to request a Drive access sync for workgroup {WorkgroupId}", w.Id);
            }
        }

        // Only claimed groups: a scoped sync of an unclaimed key is recorded as an error.
        if (w.GoogleGroupEmail is { } groupKey && WorkgroupGoogleGroupSource.IsClaimed(w.Status))
        {
            try
            {
                await googleGroupSync.RequestSyncAsync(groupKey, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to request a Google group sync for workgroup {WorkgroupId}", w.Id);
            }
        }
    }

    internal async Task AuditAsync(
        AuditActionDefinition action,
        Workgroup w,
        string description,
        Guid actorUserId,
        string entityType = AuditEntityTypes.Workgroup,
        Guid? entityId = null)
    {
        var id = entityId ?? w.Id;
        await auditLog.LogAsync(action, entityType, id, description, actorUserId,
            relatedEntityId: id == w.Id ? null : w.Id,
            relatedEntityType: id == w.Id ? null : AuditEntityTypes.Workgroup);
    }

    internal static WorkgroupLogEntry SystemEntry(
        Guid workgroupId,
        WorkgroupLogKind kind,
        Instant now,
        string? body,
        Guid? authorUserId = null,
        Guid? documentId = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            Kind = kind,
            OccurredOn = now.InUtc().Date,
            Body = Trimmed(body),
            AuthorUserId = authorUserId,
            DocumentId = documentId,
            CreatedAt = now,
            UpdatedAt = now
        };

    internal static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var existing = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null);
        if (existing is not null)
            return new(Refusal: new(existing.IsPending == true ? WorkgroupErrorKeys.MembershipPending : WorkgroupErrorKeys.AlreadyAMember));

        var now = clock.GetCurrentInstant();
        await repository.AddMemberAsync(
            NewMember(workgroup.Id, userId, WorkgroupMemberRole.Member, now, isPending: true), ct);
        // Committed: the notice must survive a closed tab. It names nobody: the requester's name lives only on the
        // pending row, which erasure deletes, never in an inbox it can't reach.
        ct = CancellationToken.None;
        var info = ToInfo(workgroup);
        await NotifyAsync(info.CoordinatorUserIds(), WorkgroupsNotificationSources.WorkgroupMembershipRequested,
            "Workgroups_Notice_MembershipRequested", info, body: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ApproveMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        return await AdmitAsync(workgroup, pending, actorUserId, WorkgroupsAuditActions.WorkgroupMembershipApproved,
            "Workgroups_Notice_MembershipApproved", ct);
    }

    public async Task<WorkgroupMutationResult> DeclineMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        var name = await NameOfAsync(userId, ct);
        pending.LeftAt = clock.GetCurrentInstant();
        await repository.UpdateMembersAsync([pending], ct);
        ct = CancellationToken.None;
        await auditLog.LogAsync(WorkgroupsAuditActions.WorkgroupMembershipDeclined, AuditEntityTypes.Workgroup, workgroup.Id,
            $"Declined the join request of {name}", actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
        await NotifyAsync([userId], WorkgroupsNotificationSources.WorkgroupMembershipDecided,
            "Workgroups_Notice_MembershipDeclined", ToInfo(workgroup), body: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> AddMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        var target = await ResolveMemberTargetAsync(userId, ct);
        if (target is null)
            return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        userId = target.Value;

        var existing = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null);
        if (existing is { IsPending: not true })
            return new(Refusal: new(WorkgroupErrorKeys.AlreadyAMember));

        // A pending request from the same person is approved by the add.
        var member = existing ?? NewMember(workgroup.Id, userId, WorkgroupMemberRole.Member, clock.GetCurrentInstant(), isPending: true);
        return await AdmitAsync(workgroup, member, actorUserId, WorkgroupsAuditActions.WorkgroupMemberAdded,
            "Workgroups_Notice_MemberAdded", ct, isNew: existing is null);
    }

    /// <summary>The one place a person becomes a member.</summary>
    private async Task<WorkgroupMutationResult> AdmitAsync(
        Workgroup workgroup, WorkgroupMember member, Guid actorUserId, AuditActionDefinition auditAction,
        string noticeKey, CancellationToken ct, bool isNew = false)
    {
        var name = await NameOfAsync(member.UserId, ct);
        var now = clock.GetCurrentInstant();
        member.IsPending = null;
        member.JoinedAt = now;
        var entries = new[] { SystemEntry(workgroup.Id, WorkgroupLogKind.MemberJoined, now, name) };
        if (isNew) await repository.AddMemberAsync(member, ct, entries);
        else await repository.UpdateMembersAsync([member], ct, entries);
        ct = CancellationToken.None;
        // Related entity is the person admitted: their GDPR export selects audit rows by that column.
        await auditLog.LogAsync(auditAction, AuditEntityTypes.Workgroup, workgroup.Id,
            $"{name} admitted to the group", actorUserId,
            relatedEntityId: member.UserId, relatedEntityType: AuditSubjectTypes.User);
        await NotifyAsync([member.UserId], WorkgroupsNotificationSources.WorkgroupMembershipDecided,
            noticeKey, ToInfo(workgroup), body: null, ct);
        await RequestGoogleSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var leaving = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null);
        if (leaving is null) return new(Refusal: new(WorkgroupErrorKeys.NotAMember));

        var now = clock.GetCurrentInstant();
        var changed = new List<WorkgroupMember> { leaving };
        leaving.LeftAt = now;

        if (leaving.IsPending == true)
        {
            await repository.UpdateMembersAsync(changed, ct);
            return new();
        }

        var remainingCoordinators = workgroup.Members
            .Where(m => m.LeftAt is null && m.Id != leaving.Id && m.Role == WorkgroupMemberRole.Coordinator)
            .ToList();

        WorkgroupMember? promoted = null;
        if (leaving.Role == WorkgroupMemberRole.Coordinator && remainingCoordinators.Count == 0)
        {
            if (replacementCoordinatorUserId is { } replacementId)
            {
                promoted = workgroup.Members
                    .FirstOrDefault(m => m.UserId == replacementId && m.LeftAt is null && m.IsPending != true && m.Id != leaving.Id);
                if (promoted is null) return new(Refusal: new(WorkgroupErrorKeys.CoordinatorsMustBeMembers));
                promoted.Role = WorkgroupMemberRole.Coordinator;
                changed.Add(promoted);
            }
            else
            {
                return new(Refusal: new(WorkgroupErrorKeys.LastCoordinatorNeedsReplacement));
            }
        }

        var leavingName = await NameOfAsync(userId, ct);
        var promotedName = promoted is null ? null : await NameOfAsync(promoted.UserId, ct);
        var entries = new List<WorkgroupLogEntry>
        {
            SystemEntry(workgroup.Id, WorkgroupLogKind.MemberLeft, now, leavingName)
        };
        if (promoted is not null)
            entries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.CoordinatorChanged, now, promotedName));
        await repository.UpdateMembersAsync(changed, ct, entries);
        ct = CancellationToken.None;
        if (promoted is not null)
        {
            await AuditAsync(WorkgroupsAuditActions.WorkgroupCoordinatorsChanged, workgroup,
                $"Coordination handed to {promotedName} when the last coordinator left",
                userId);
        }

        await RequestGoogleSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinatorUserIds);
        var result = await RequireActiveAsync(workgroupId, ct);
        return result.Refusal is { } refusal ? new(Refusal: refusal)
            : await ApplyCoordinatorsAsync(result.Value!, actorUserId, coordinatorUserIds, actorUserId, ct);
    }

    public async Task<WorkgroupMutationResult> AppointCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinatorUserIds);
        var result = await RequireAsync(workgroupId, ct);
        return result.Refusal is { } refusal ? new(Refusal: refusal)
            : await ApplyCoordinatorsAsync(result.Value!, actorUserId, coordinatorUserIds, null, ct);
    }

    private async Task<WorkgroupMutationResult> ApplyCoordinatorsAsync(
        Workgroup workgroup, Guid actorUserId, IReadOnlyList<Guid> coordinatorUserIds,
        Guid? logAuthorUserId, CancellationToken ct)
    {
        var wanted = coordinatorUserIds.Distinct().ToList();
        if (wanted.Count is < 1 or > 2)
            return new(Refusal: new(WorkgroupErrorKeys.CoordinatorCount));

        var current = workgroup.Members.Where(m => m.LeftAt is null && m.IsPending != true).ToList();
        if (wanted.Exists(id => current.TrueForAll(m => m.UserId != id)))
            return new(Refusal: new(WorkgroupErrorKeys.CoordinatorsMustBeMembers));

        var now = clock.GetCurrentInstant();
        var changed = new List<WorkgroupMember>();
        foreach (var member in current)
        {
            var role = wanted.Contains(member.UserId)
                ? WorkgroupMemberRole.Coordinator
                : WorkgroupMemberRole.Member;
            if (member.Role == role)
                continue;

            member.Role = role;
            changed.Add(member);
        }

        if (changed.Count == 0)
            return new();

        var names = await NamesOfAsync(wanted, ct);
        await repository.UpdateMembersAsync(changed, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.CoordinatorChanged, now,
                string.Join(", ", names), authorUserId: logAuthorUserId)]);
        ct = CancellationToken.None;
        await AuditAsync(WorkgroupsAuditActions.WorkgroupCoordinatorsChanged, workgroup,
            $"Coordinators set to {string.Join(", ", names)}", actorUserId);

        var info = ToInfo(workgroup);
        var affected = changed.Select(m => m.UserId).Distinct().ToList();
        await NotifyAsync(affected, WorkgroupsNotificationSources.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_CoordinatorChanged", info, string.Join(", ", names), ct);
        await EmailAsync(affected, WorkgroupNoticeKind.CoordinatorsChanged, info,
            string.Join(", ", names), ct);
        return new();
    }

    internal static WorkgroupMember NewMember(
        Guid workgroupId, Guid userId, WorkgroupMemberRole role, Instant now, bool isPending = false) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            UserId = userId,
            Role = role,
            JoinedAt = now,
            IsPending = isPending ? true : null
        };

    private async Task<string> NameOfAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info?.BurnerName ?? "A member";
    }

    private async Task<IReadOnlyList<string>> NamesOfAsync(IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var infos = await users.GetUserInfosAsync(userIds, ct);
        return userIds
            .Select(id => infos.TryGetValue(id, out var info) ? info.BurnerName : "A member")
            .ToList();
    }

    private static string PageUrl(WorkgroupInfo w) => $"/Workgroups/{w.Slug}";

    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(WorkgroupsResource));

    // Returns successful dispatch requests for the rhythm audit; Notifications applies inbox preferences.
    internal async Task<int> NotifyAsync(
        IReadOnlyList<Guid> recipientUserIds, NotificationSourceDefinition source, string titleKey,
        WorkgroupInfo w, string? body, CancellationToken ct)
    {
        if (recipientUserIds.Count == 0) return 0;
        IReadOnlyDictionary<Guid, UserInfo> people;
        try
        {
            people = await users.GetUserInfosAsync(recipientUserIds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve workgroup {Source} notification recipients for {WorkgroupId}", source, w.Id);
            return 0;
        }
        // In-app notices are keyed by user id: deliver to the live id the read resolved to.
        var recipients = recipientUserIds.Select(id =>
        {
            var person = people.GetValueOrDefault(id);
            var language = person?.PreferredLanguage;
            return (Id: person?.Id ?? id, Language: language.IsSupportedCultureCode() ? language! : "en");
        }).DistinctBy(person => person.Id);
        var dispatched = 0;
        foreach (var group in recipients.GroupBy(person => person.Language, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(group.Key);
                var title = $"{NoticeResources.GetString(titleKey, culture)}: {w.Name}";
                var ids = group.Select(person => person.Id).ToList();
                await notificationEmitter.SendAsync(source, NotificationClass.Informational, NotificationPriority.Normal,
                    title, ids, body,
                    actionUrl: PageUrl(w), sourceKey: w.Id.ToString(), cancellationToken: ct);
                dispatched += ids.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send workgroup {Source} notification in {Language} for {WorkgroupId}",
                    source, group.Key, w.Id);
            }
        }
        return dispatched;
    }

    internal async Task NotifyBoardAsync(
        NotificationSourceDefinition source, string title, WorkgroupInfo w, string body, CancellationToken ct)
    {
        try
        {
            await notifications.SendToRoleAsync(source, NotificationClass.Actionable,
                NotificationPriority.Normal, title, RoleNames.Board, body,
                actionUrl: "/Workgroups/Admin", cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send workgroup {Source} notification to the Board for {WorkgroupId}", source, w.Id);
        }
    }

    /// <summary>Email failures never fail the write: the log and audit are the record, the email a courtesy.</summary>
    internal async Task EmailAsync(
        IReadOnlyList<Guid> recipientUserIds,
        WorkgroupNoticeKind kind,
        WorkgroupInfo w,
        string? detail,
        CancellationToken ct)
    {
        var ids = recipientUserIds.Distinct().ToList();
        if (ids.Count == 0)
            return;

        IReadOnlyDictionary<Guid, UserInfo> infos;
        try
        {
            infos = await users.GetUserInfosAsync(ids, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve recipients for the workgroup {Kind} notice on {WorkgroupSlug}",
                kind, w.Slug);
            return;
        }

        var recipientIds = new HashSet<Guid>();
        foreach (var id in ids)
        {
            infos.TryGetValue(id, out var info);
            var address = info?.Email;
            if (info is null || string.IsNullOrWhiteSpace(address))
            {
                logger.LogWarning(
                    "Skipping workgroup {Kind} email for {UserId} on {WorkgroupId}: no effective email",
                    kind, id, w.Id);
                continue;
            }
            if (!recipientIds.Add(info.Id)) continue;

            var language = info.PreferredLanguage;
            await SendNoticeAsync(new WorkgroupNoticeRequest(
                info.Id, address, info.BurnerName, kind, w.Name, w.Slug, detail,
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
        }
    }

    internal async Task EmailBoardAsync(
        WorkgroupNoticeKind kind, WorkgroupInfo w, string? detail, CancellationToken ct)
    {
        try
        {
            var boardUserIds = await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);
            await EmailAsync(boardUserIds, kind, w, detail, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve the Board for the workgroup {Kind} notice on {WorkgroupSlug}",
                kind, w.Slug);
        }
    }

    private async Task SendNoticeAsync(WorkgroupNoticeRequest request, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(emailFactory.WorkgroupNotice(request), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue the workgroup {Kind} notice for {WorkgroupSlug}",
                request.Kind, request.WorkgroupSlug);
        }
    }

    public async Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default)
    {
        var graph = await repository.GetGraphAsync(ct);
        return graph.Workgroups.Select(ToInfo).ToList();
    }

    public async Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.Ordinal));

    public async Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var workgroup = await repository.GetWorkgroupAsync(id, ct);
        return workgroup is null ? null : ToInfo(workgroup);
    }

    public async Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default)
    {
        var register = await GetRegisterAsync(ct);
        return register.Where(w => w.IsMember(userId)).ToList();
    }

    public static WorkgroupInfo ToInfo(Workgroup w) => new(
        w.Id,
        w.Slug,
        w.Name,
        w.Purpose,
        w.Deliverable,
        w.DeliverableKind,
        w.Audience,
        w.TargetDate,
        w.Status,
        w.DormantReason,
        w.Reasons,
        w.DriveFolderId,
        SafeHttpLink(w.DiscordChannelUrl),
        w.AppliedAt,
        w.RegisteredAt,
        w.EndedAt,
        w.Members
            .Select(m => new WorkgroupMemberInfo(m.UserId, m.Role, m.JoinedAt, m.LeftAt, m.IsPending == true))
            .ToList(),
        // Soft-deleted meetings are gone outside the repository; the log keeps the history.
        w.Meetings
            .Where(m => m.DeletedAt is null)
            .Select(m => new WorkgroupMeetingInfo(
                m.Id, m.Title, m.StartUtc, m.EndUtc, m.Location, SafeHttpLink(m.LocationUrl), m.IsPublic,
                m.Minutes, m.CreatedByUserId, m.Description,
                m.RecurrenceRule, m.RecurrenceTimezone))
            .ToList(),
        w.LogEntries
            .Select(e => new WorkgroupLogEntryInfo(
                e.Id, e.Kind, e.OccurredOn, e.Title, e.Body, e.AuthorUserId, e.CreatedAt, e.MeetingId))
            .ToList(),
        w.Documents
            .Select(ToInfo)
            .ToList(),
        w.BudgetAmount,
        w.HoldedAccountNumber,
        w.HoldedAccountId,
        w.IsSensitive == true,
        w.GoogleGroupEmail);

    public static WorkgroupDocumentInfo ToInfo(WorkgroupDocument d) => new(
        d.Id,
        d.Title,
        d.Kind,
        d.Body,
        d.Status,
        d.CommentCategories.ToList(),
        d.CommentsOpenAt,
        d.CommentsCloseAt,
        d.DeliveredAt,
        d.Disposition,
        d.DispositionNote,
        d.CreatedByUserId,
        d.UpdatedByUserId,
        d.CreatedAt,
        d.UpdatedAt,
        d.Comments
            .Select(c => new WorkgroupCommentInfo(
                c.Id, c.Category, c.AuthorUserId, c.Body, c.CreatedAt,
                c.Disposition, c.Response, c.HiddenAt,
                c.HiddenReason))
            .ToList());

    // Also guards stored legacy links when projecting them into an href.
    internal static string? SafeHttpLink(string? url)
    {
        var trimmed = url?.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? trimmed : null;
    }

    public async Task RunDailyRhythmAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var graph = await repository.GetGraphAsync(ct);

        foreach (var workgroup in graph.Workgroups)
        {
            ct.ThrowIfCancellationRequested();
            var info = WorkgroupService.ToInfo(workgroup);

            // One group's failure isn't the pass's: the rest still get their 
            try
            {
                if (info.Status == WorkgroupStatus.Active)
                {
                    await NudgeForUpdateAsync(workgroup, info, now, ct);
                }
                else if (info.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred)
                {
                    await FlagOverdueRegistrationAsync(workgroup, info, now, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogWarning("The workgroup rhythm pass was cancelled while processing {WorkgroupId}", workgroup.Id);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The workgroup rhythm pass failed for {WorkgroupId}", workgroup.Id);
            }
        }
    }

    /// <summary>In-app only.</summary>
    private async Task NudgeForUpdateAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (info.SilenceFor(now) is not { } silence || !info.IsUpdateDue(now))
            return;

        // Once per thirty-day window (day 30, 60, 90 …); the register badge carries it in between.
        var days = (int)silence.TotalDays;
        if (days % (int)WorkgroupInfo.UpdateDueAfter.TotalDays != 0)
            return;

        var coordinators = info.CoordinatorUserIds();
        var dispatched = await NotifyAsync(coordinators, WorkgroupsNotificationSources.WorkgroupReportingDue,
            "Workgroups_Todo_UpdateDue_Title", info, body: null, ct);
        if (dispatched > 0)
            await AuditJobAsync(WorkgroupsAuditActions.WorkgroupUpdateDueNotified,
                workgroup, $"Requested update this for {dispatched} coordinators after {days} days of silence");
    }

    /// <summary>The Board is told once; the queue keeps showing it.</summary>
    private async Task FlagOverdueRegistrationAsync(
        Workgroup workgroup, WorkgroupInfo info, Instant now, CancellationToken ct)
    {
        if (!info.IsRegistrationOverdue(now)
            || !JustCrossed(now - info.AppliedAt, WorkgroupInfo.RegistrationDueAfter))
        {
            return;
        }

        await AuditJobAsync(WorkgroupsAuditActions.WorkgroupApplicationOverdue,
            workgroup, "Application still undecided fourteen days after it was made");
        await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
            $"Application overdue: {workgroup.Name}", info,
            "Clause 1 gives the Secretary fourteen days. This application is past that.", ct);
    }

    /// <summary>
    /// True on the one daily pass that first sees <paramref name="elapsed"/> past <paramref name="threshold"/>.
    /// The queue row is the durable signal, so a missed pass costs only a nudge.
    /// </summary>
    private static bool JustCrossed(Duration elapsed, Duration threshold) =>
        elapsed >= threshold && elapsed - threshold < Duration.FromDays(1);
    private async Task AuditJobAsync(AuditActionDefinition action, Workgroup w, string description)
    {
        await auditLog.LogAsync(action, AuditEntityTypes.Workgroup, w.Id, description,
            WorkgroupRhythmJobName);
    }

    internal const string WorkgroupRhythmJobName = "WorkgroupRhythmJob";
}
