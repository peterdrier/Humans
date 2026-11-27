using Humans.Email.Contracts;
using Humans.Grants.Data;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using NodaTime;
using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Grants.Domain;
using Humans.Base.Extensions;
using Humans.Grants.Contracts;
using Humans.Gdpr.Contracts;

namespace Humans.Grants.Services;

/// <summary>Nothing runs on a timer: expiry is a date compared when read.</summary>
internal sealed class GrantService(
    IOrgTimeZone orgTimeZone,
    IGrantRepository repository,
    IUserService users,
    INotificationEmitter notifications,
    IEmailService email,
    GrantsEmails emailFactory,
    IAuditLogService auditLog,
    ISettingsService settingsService,
    IClock clock,
    ILogger<GrantService> logger) : IGrantManagement, IGrantService, IUserDataContributor, IUserMerge
{
    private DateTimeZone OrgZone => orgTimeZone.Zone;

    public LocalDate Today => clock.GetCurrentInstant().InZone(OrgZone).Date;

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default)
    {
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        return (await repository.GetProgramsAsync(ct)).Select(p => ToInfo(p, events)).ToList();
    }

    public async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program, await settingsService.GetAllEventSettingsAsync(ct));
    }

    public async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct);
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        return grants.Select(g => ToInfo(g, g.Program, EventOf(g.Program, events))).ToList();
    }

    public async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        await repository.ManagesAnyProgramAsync(await AllIdsAsync(userId, ct), ct);

    private static ProgramInfo ToInfo(GrantProgram p, IReadOnlyList<EventSettingsInfo> events)
    {
        var evt = EventOf(p, events);
        return new(
            p.Id, p.Name, p.Entitles, p.Description, evt?.Year ?? p.Year, evt?.Id ?? p.EventSettingsId,
            evt?.EventName ?? p.Year.ToString(CultureInfo.InvariantCulture),
            p.IssuedBy, p.SourceKey, p.GrantsExpireOn,
            p.Status,
            p.Managers.Select(m => m.UserId).ToList(),
            p.Grants.Select(g => ToInfo(g, p, evt)).ToList());
    }

    internal static EventSettingsInfo? EventOf(GrantProgram p, IReadOnlyList<EventSettingsInfo> events) =>
        p.EventSettingsId is { } id ? events.FirstOrDefault(e => e.Id == id) : null;

    internal static int YearOf(GrantProgram p, IReadOnlyList<EventSettingsInfo> events) => EventOf(p, events)?.Year ?? p.Year;

    internal async Task<int> YearOfAsync(GrantProgram p, CancellationToken ct) =>
        YearOf(p, await settingsService.GetAllEventSettingsAsync(ct));

    /// <summary>The year comes from the event, which a gate-opening change can move to another year.</summary>
    private static GrantInfo ToInfo(Grant g, GrantProgram p, EventSettingsInfo? evt) => new(
        g.Id, g.ProgramId, p.Name, p.Entitles, evt?.Year ?? p.Year, g.UserId, g.SourceRef,
        g.ExpiresOn ?? p.GrantsExpireOn, g.IssuedAt, g.IssuedByUserId, g.NotifiedAt, g.RevokedAt,
        g.RevokedByUserId, g.RevokeReason);

    /// <summary>The survivor id first, followed by every merged id, so writes and holding checks identify one person.</summary>
    internal async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

    internal async Task<IReadOnlyList<Guid>?> ResolveAvailablePersonIdsAsync(Guid userId, CancellationToken ct)
    {
        var person = await users.GetUserInfoAsync(userId, ct);
        if (person is not null && !person.IsTombstone) return person.AllUserIds;

        logger.LogWarning("Grant write refused for unavailable person {UserId}", userId);
        return null;
    }

    internal static bool IsOpenSource(GrantProgram program, string sourceKey) =>
        program.IssuedBy == GrantIssuer.Source && program.Status == GrantProgramStatus.Open
        && string.Equals(program.SourceKey, sourceKey, StringComparison.Ordinal);

    internal static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal Task AuditGrantAsync(AuditActionDefinition action, Grant grant, string description, Guid actorUserId) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: AuditSubjectTypes.User);

    internal Task AuditProgramAsync(
        AuditActionDefinition action, GrantProgram program, string description, Guid actorUserId, Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditSubjectTypes.User);

    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(GrantsResource));

    internal const string ProfileUrl = "/Profile/Me";

    internal Task AnnounceIssuedAsync(Grant grant, GrantProgram program, CancellationToken ct) =>
        AnnounceAsync(grant, program, GrantNoticeKind.Issued, GrantsNotificationSources.GrantReceived, ct);

    internal Task AnnounceRevokedAsync(Grant grant, GrantProgram program, CancellationToken ct) =>
        AnnounceAsync(grant, program, GrantNoticeKind.Revoked, GrantsNotificationSources.GrantRevoked, ct);

    private async Task AnnounceAsync(
        Grant grant, GrantProgram program, GrantNoticeKind kind, NotificationSourceDefinition source, CancellationToken ct,
        UserInfo? person = null, bool recipientLookupAttempted = false)
    {
        // Each step is best-effort on its own: one channel's outage never suppresses the other.
        if (!recipientLookupAttempted)
            await BestEffortAsync("recipient lookup", kind, grant.Id,
                async () => person = await users.GetUserInfoAsync(grant.UserId, ct), ct);
        var culture = CultureOf(person);
        var expiresOn = grant.ExpiresOn ?? program.GrantsExpireOn;

        await BestEffortAsync("in-app notice", kind, grant.Id,
            () => NotifyAsync(person?.Id ?? grant.UserId, culture, kind, source, program, expiresOn, grant.RevokeReason, ct), ct);

        await BestEffortAsync("email", kind, grant.Id, async () =>
        {
            // Reuse a successful lookup; email still gets its independent attempt after a lookup failure.
            var recipient = person ?? await users.GetUserInfoAsync(grant.UserId, ct);
            if (recipient is null || recipient.IsTombstone || string.IsNullOrWhiteSpace(recipient.Email))
            {
                logger.LogWarning("Skipping grant {Kind} email for {UserId} on grant {GrantId}: unavailable recipient or no effective email",
                    kind, grant.UserId, grant.Id);
                return;
            }
            await email.SendAsync(emailFactory.Notice(new GrantNoticeRequest(
                recipient.Id, recipient.Email, recipient.BurnerName, kind, program.Name, program.Entitles, expiresOn,
                grant.RevokeReason, CultureOf(recipient))), ct);
        }, ct);
    }

    private static string CultureOf(UserInfo? person) =>
        person?.PreferredLanguage is { } language && language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode;

    private async Task BestEffortAsync(string step, GrantNoticeKind kind, Guid grantId, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await send();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed the grant {Kind} {Step} for grant {GrantId}", kind, step, grantId);
        }
    }

    private async Task NotifyAsync(
        Guid recipientId, string cultureName, GrantNoticeKind kind, NotificationSourceDefinition source, GrantProgram program,
        LocalDate? expiresOn, string? reason, CancellationToken ct)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        using var cultureScope = new CultureScope(culture.Name, logger);
        var titleKey = kind == GrantNoticeKind.Issued ? "Grants_Notice_Issued" : "Grants_Notice_Revoked";
        var title = $"{NoticeResources.GetString(titleKey, culture)}: {program.Name}";
        var body = kind == GrantNoticeKind.Issued
            ? string.Format(culture, NoticeResources.GetString("Grants_Notice_Issued_Body", culture)!, program.Entitles)
            : string.Format(culture, NoticeResources.GetString("Grants_Notice_Revoked_Body", culture)!, program.Entitles);
        if (kind == GrantNoticeKind.Issued && expiresOn is { } expires)
            body += " " + string.Format(culture, NoticeResources.GetString("Grants_Notice_ExpiresOn", culture)!, expires.ToDate());
        if (kind == GrantNoticeKind.Revoked && !string.IsNullOrEmpty(reason))
            body += " " + reason;
        await notifications.SendAsync(source, NotificationClass.Informational, NotificationPriority.Normal, title,
            [recipientId], body, actionUrl: ProfileUrl, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ActiveGrantInfo>> GetActiveGrantsAsync(Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct);
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        var today = Today;
        return grants
            .Select(g => (Grant: g, Info: ToInfo(g, g.Program, EventOf(g.Program, events))))
            .Where(x => x.Info.State(today) == GrantState.Active)
            .Select(x => new ActiveGrantInfo(x.Grant.ProgramId, x.Grant.Program.EventSettingsId, x.Grant.IssuedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<GrantProgramSummary>> GetProgramSummariesAsync(CancellationToken ct = default) =>
        (await GetProgramsAsync(ct))
            .OrderByDescending(p => p.Year).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new GrantProgramSummary(p.Id, p.Name, p.EventName))
            .ToList();

    private const int NameMax = 200;
    private const int EntitlesMax = 500;
    private const int DescriptionMax = 2000;
    private const int SourceKeyMax = 32;

    public async Task<GrantMutationResult<Guid>> CreateProgramAsync(
        ProgramSave save, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (Validate(save) is { } invalid) return new(Refusal: invalid);
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        if (ChosenEvent(save, events) is not { } evt) return new(Refusal: NoEvent);
        if (await SourceTakenAsync(save, programId: null, ct) is { } refusal) return new(Refusal: refusal);
        var managerIds = await ResolveManagerIdsAsync(save.ManagerUserIds, ct);
        if (managerIds is null) return new(Refusal: "One or more selected managers are no longer available.");

        var now = clock.GetCurrentInstant();
        var program = new GrantProgram
        {
            Id = Guid.NewGuid(),
            Name = save.Name.Trim(),
            Entitles = save.Entitles.Trim(),
            Description = Trimmed(save.Description),
            Year = evt.Year,
            EventSettingsId = evt.Id,
            IssuedBy = save.IssuedBy,
            SourceKey = SourceKeyOf(save),
            GrantsExpireOn = save.GrantsExpireOn,
            Status = save.Status,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var userId in managerIds)
            program.Managers.Add(new GrantProgramManager
            { ProgramId = program.Id, UserId = userId, AddedByUserId = actorUserId, AddedAt = now });
        await repository.AddProgramAsync(program, ct);

        await AuditProgramAsync(GrantsAuditActions.GrantProgramCreated, program,
            $"Program \"{program.Name}\" ({evt.EventName}) created: entitles \"{program.Entitles}\", " +
            $"issued by {IssuerText(program.IssuedBy, program.SourceKey)}, " +
            $"expires {program.GrantsExpireOn?.ToInvariantDate() ?? "never"}, {program.Status}.",
            actorUserId);
        foreach (var manager in program.Managers)
            await AuditProgramAsync(GrantsAuditActions.GrantProgramManagerAdded, program,
                $"Manager added to \"{program.Name}\".", actorUserId, relatedUserId: manager.UserId);

        return new(program.Id);
    }

    public async Task<GrantMutationResult> UpdateProgramAsync(
        Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new("Program not found.");
        if (Validate(save) is { } invalid) return new(invalid);
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        if (ChosenEvent(save, events) is not { } evt) return new(NoEvent);
        if (await SourceTakenAsync(save, programId, ct) is { } refusal) return new(refusal);
        var wanted = await ResolveManagerIdsAsync(save.ManagerUserIds, ct);
        if (wanted is null) return new("One or more selected managers are no longer available.");

        var changes = new List<string>();
        Change(changes, "name", program.Name, save.Name.Trim(), v => program.Name = v);
        Change(changes, "entitles", program.Entitles, save.Entitles.Trim(), v => program.Entitles = v);
        Change(changes, "description", program.Description, Trimmed(save.Description), v => program.Description = v);
        // By id: two events may share a name.
        if (EventOf(program, events) is var before && before?.Id != evt.Id)
            changes.Add($"event {before?.EventName ?? program.Year.ToString(CultureInfo.InvariantCulture)} → {evt.EventName}");
        program.EventSettingsId = evt.Id;
        program.Year = evt.Year;
        Change(changes, "issued by", IssuerText(program.IssuedBy, program.SourceKey), IssuerText(save.IssuedBy, SourceKeyOf(save)), _ =>
        {
            program.IssuedBy = save.IssuedBy;
            program.SourceKey = SourceKeyOf(save);
        });
        Change(changes, "expires", program.GrantsExpireOn, save.GrantsExpireOn, v => program.GrantsExpireOn = v,
            d => d?.ToInvariantDate() ?? "never");
        Change(changes, "status", program.Status, save.Status, v => program.Status = v);

        var now = clock.GetCurrentInstant();
        program.UpdatedAt = now;
        var current = program.Managers.Select(m => m.UserId).ToList();
        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        await repository.UpdateProgramAsync(program,
            added.Select(id => new GrantProgramManager { UserId = id, AddedByUserId = actorUserId, AddedAt = now }).ToList(),
            removed, ct);

        if (changes.Count > 0)
            await AuditProgramAsync(GrantsAuditActions.GrantProgramEdited, program,
                $"Program \"{program.Name}\" edited: {string.Join("; ", changes)}.", actorUserId);
        foreach (var id in added)
            await AuditProgramAsync(GrantsAuditActions.GrantProgramManagerAdded, program,
                $"Manager added to \"{program.Name}\".", actorUserId, relatedUserId: id);
        foreach (var id in removed)
            await AuditProgramAsync(GrantsAuditActions.GrantProgramManagerRemoved, program,
                $"Manager removed from \"{program.Name}\".", actorUserId, relatedUserId: id);

        return GrantMutationResult.Ok;
    }

    private async Task<IReadOnlyList<Guid>?> ResolveManagerIdsAsync(IReadOnlyList<Guid> requested, CancellationToken ct)
    {
        var managers = new List<Guid>();
        foreach (var id in requested.Distinct())
        {
            var personIds = await ResolveAvailablePersonIdsAsync(id, ct);
            if (personIds is null) return null;
            managers.Add(personIds[0]);
        }
        return managers.Distinct().ToList();
    }

    private static string? Validate(ProgramSave save)
    {
        if (string.IsNullOrWhiteSpace(save.Name)) return "A name is required.";
        if (string.IsNullOrWhiteSpace(save.Entitles)) return "Say what a grant entitles the person to.";
        if (save.Name.Trim().Length > NameMax || save.Entitles.Trim().Length > EntitlesMax
            || (save.Description?.Length ?? 0) > DescriptionMax)
            return "A field is too long.";
        if (!Enum.IsDefined(save.IssuedBy)) return "The issuer is out of range.";
        if (!Enum.IsDefined(save.Status)) return "The status is out of range.";
        if (save.IssuedBy == GrantIssuer.Source)
        {
            if (string.IsNullOrWhiteSpace(save.SourceKey)) return "A source program needs the key of the section that fills it.";
            if (save.SourceKey.Trim().Length > SourceKeyMax) return "The source key is too long.";
        }
        return null;
    }

    /// <summary>At most one open program per source key, so a source write always knows its program.</summary>
    private async Task<string?> SourceTakenAsync(ProgramSave save, Guid? programId, CancellationToken ct)
    {
        if (save.IssuedBy != GrantIssuer.Source || save.Status != GrantProgramStatus.Open) return null;
        var key = SourceKeyOf(save)!;
        var other = (await repository.GetProgramsAsync(ct))
            .FirstOrDefault(p => p.Id != programId && IsOpenSource(p, key));
        return other is null ? null : $"\"{other.Name}\" is already the open program for {key}. Close it first.";
    }

    private const string NoEvent = "Pick an event.";

    private static EventSettingsInfo? ChosenEvent(ProgramSave save, IReadOnlyList<EventSettingsInfo> events) =>
        events.FirstOrDefault(e => e.Id == save.EventSettingsId && e.Status != EventSettingsStatus.Deleted);

    private static string? SourceKeyOf(ProgramSave save) =>
        save.IssuedBy == GrantIssuer.Source ? Trimmed(save.SourceKey) : null;

    private static string IssuerText(GrantIssuer issuer, string? sourceKey) =>
        issuer == GrantIssuer.Source ? $"source {sourceKey}" : "managers";

    private static void Change<T>(
        List<string> changes, string field, T before, T after, Action<T> apply, Func<T, string>? show = null)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        show ??= v => v?.ToString() ?? "none";
        changes.Add($"{field} {show(before)} → {show(after)}");
        apply(after);
    }

    private const int ReasonMax = 500;
    private const int SourceRefMax = 128;

    public async Task<GrantMutationResult<IReadOnlyList<Guid>>> IssueToAsync(
        Guid programId, IReadOnlyList<Guid> userIds, LocalDate? expiresOn, Guid actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new(Refusal: "Program not found.");
        if (program.Status == GrantProgramStatus.Closed) return new(Refusal: "The program is closed.");
        if (program.IssuedBy == GrantIssuer.Source)
            return new(Refusal: $"This program is filled by {program.SourceKey}, not by hand.");
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new(Refusal: "Pick at least one person.");

        var recipients = new List<IReadOnlyList<Guid>>();
        foreach (var id in ids)
        {
            var personIds = await ResolveAvailablePersonIdsAsync(id, ct);
            if (personIds is null) return new(Refusal: "One or more selected people are no longer available.");
            recipients.Add(personIds);
        }
        var holding = program.Grants.Where(g => g.RevokedAt is null).Select(g => g.UserId).ToHashSet();
        var now = clock.GetCurrentInstant();
        var grants = recipients.DistinctBy(personIds => personIds[0])
            .Where(personIds => !personIds.Any(holding.Contains))
            .Select(personIds => NewGrant(program, personIds[0], sourceRef: null, expiresOn, actorUserId, now))
            .ToList();
        if (grants.Count == 0) return new(Value: []);

        await PersistIssuedGrantsAsync(program, grants, actorUserId,
            (grant, year) => $"Issued under \"{program.Name}\" ({year}){ExpirySuffix(grant, program)}" +
                $"{(grants.Count > 1 ? $" (batch of {grants.Count})" : "")}.", ct);
        return new(grants.Select(g => g.Id).ToList());
    }

    public async Task<GrantMutationResult> RevokeGrantAsync(
        Guid programId, Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null || grant.ProgramId != programId) return new("Grant not found.", NotFound: true);
        if (grant.RevokedAt is not null) return new("The grant is already revoked.");
        if ((reason?.Length ?? 0) > ReasonMax) return new("The reason is too long.");

        await RevokeCoreAsync(grant, Trimmed(reason), actorUserId, ct);
        return GrantMutationResult.Ok;
    }

    public async Task<GrantMutationResult> ResendNoticeAsync(Guid programId, Guid grantId, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null || grant.ProgramId != programId) return new("Grant not found.", NotFound: true);
        if (grant.RevokedAt is not null) return new("A revoked grant has no notice to resend.");

        UserInfo? person = null;
        var recipientResolved = false;
        await BestEffortAsync("recipient lookup", GrantNoticeKind.Issued, grant.Id, async () =>
        {
            person = await users.GetUserInfoAsync(grant.UserId, ct);
            recipientResolved = true;
        }, ct);
        if (recipientResolved && (person is null || person.IsTombstone))
            return new("The grant holder is no longer available.");

        var year = await YearOfAsync(grant.Program, ct);
        grant.NotifiedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);
        await AuditGrantAsync(GrantsAuditActions.GrantNoticeResent, grant,
            $"Notice for \"{grant.Program.Name}\" ({year}) sent again.", actorUserId);
        await AnnounceAsync(grant, grant.Program, GrantNoticeKind.Issued, GrantsNotificationSources.GrantReceived,
            CancellationToken.None, person, recipientLookupAttempted: true);
        return GrantMutationResult.Ok;
    }

    public async Task<GrantIssueResult> IssueAsync(
        string sourceKey, Guid userId, string? sourceRef, Guid actorUserId, CancellationToken ct = default)
    {
        sourceRef = Trimmed(sourceRef);
        if (sourceRef?.Length > SourceRefMax)
            return new(Guid.Empty, GrantWriteRefusal.SourceReferenceTooLong);
        var program = await SourceProgramAsync(sourceKey, ct);
        if (program is null) return new(Guid.Empty, GrantWriteRefusal.NoOpenProgram);

        var personIds = await ResolveAvailablePersonIdsAsync(userId, ct);
        if (personIds is null) return new(Guid.Empty, GrantWriteRefusal.RecipientUnavailable);
        var live = program.Grants.FirstOrDefault(g => personIds.Contains(g.UserId) && g.RevokedAt is null);
        if (live is not null) return new(live.Id);

        var grant = NewGrant(program, personIds[0], sourceRef, expiresOn: null, actorUserId, clock.GetCurrentInstant());
        await PersistIssuedGrantsAsync(program, [grant], actorUserId,
            (issued, year) => $"Issued under \"{program.Name}\" ({year}) by source {program.SourceKey}" +
                $"{(sourceRef is null ? "" : $", ref {sourceRef}")}{ExpirySuffix(issued, program)}.", ct);
        return new(grant.Id);
    }

    private async Task PersistIssuedGrantsAsync(
        GrantProgram program, IReadOnlyList<Grant> grants, Guid actorUserId,
        Func<Grant, int, string> describe, CancellationToken ct)
    {
        var year = await YearOfAsync(program, ct);
        await repository.AddGrantsAsync(grants, ct);
        // The grants committed; finish their audit and notices after the browser disconnects.
        foreach (var grant in grants)
        {
            await AuditGrantAsync(GrantsAuditActions.GrantIssued, grant, describe(grant, year), actorUserId);
            await AnnounceIssuedAsync(grant, program, CancellationToken.None);
        }
    }

    public async Task<GrantRevokeResult> RevokeAsync(
        string sourceKey, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        if ((reason?.Length ?? 0) > ReasonMax)
            return new(false, GrantWriteRefusal.ReasonTooLong);
        var program = await SourceProgramAsync(sourceKey, ct);
        if (program is null) return new(false, GrantWriteRefusal.NoOpenProgram);
        var personIds = await AllIdsAsync(userId, ct);
        var live = program.Grants.FirstOrDefault(g => personIds.Contains(g.UserId) && g.RevokedAt is null);
        if (live is null) return new(false);
        live.Program = program;
        await RevokeCoreAsync(live, Trimmed(reason), actorUserId, ct);
        return new(true);
    }

    private async Task<GrantProgram?> SourceProgramAsync(string sourceKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return (await repository.GetProgramsAsync(ct)).SingleOrDefault(p => IsOpenSource(p, sourceKey));
    }

    private async Task RevokeCoreAsync(Grant grant, string? reason, Guid actorUserId, CancellationToken ct)
    {
        var year = await YearOfAsync(grant.Program, ct);
        grant.RevokedAt = clock.GetCurrentInstant();
        grant.RevokedByUserId = actorUserId;
        grant.RevokeReason = reason;
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        await AuditGrantAsync(GrantsAuditActions.GrantRevoked, grant,
            $"Revoked under \"{grant.Program.Name}\" ({year}){(reason is null ? "" : $": {reason}")}.",
            actorUserId);
        await AnnounceRevokedAsync(grant, grant.Program, ct);
    }

    private static Grant NewGrant(
        GrantProgram program, Guid userId, string? sourceRef, LocalDate? expiresOn, Guid actorUserId, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProgramId = program.Id,
            Program = program,
            UserId = userId,
            SourceRef = sourceRef,
            ExpiresOn = expiresOn,
            IssuedAt = now,
            IssuedByUserId = actorUserId,
            NotifiedAt = now
        };

    private static string ExpirySuffix(Grant grant, GrantProgram program) =>
        (grant.ExpiresOn ?? program.GrantsExpireOn) is { } expires ? $", expires {expires.ToInvariantDate()}" : "";

    internal const string Grants = "Grants";
    internal const string GrantDecisions = "GrantDecisions";

    private const string ActorRetention =
        "Manager rows are deleted. The person's id stays on programs they created, managers they named " +
        "and grants they issued or revoked, as the record of who decided, which the Board relies on (GDPR Art. 6(1)(f)); " +
        "it is a bare id that no longer resolves to a person once the account is anonymized.";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Grants] = null,
            [GrantDecisions] = ActorRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await repository.GetRowsForUsersAsync(await AllIdsAsync(userId, ct), ct);
        var events = await settingsService.GetAllEventSettingsAsync(ct);

        var grants = rows.Grants.Select(g => new
        {
            Program = g.Program.Name,
            Year = YearOf(g.Program, events),
            g.Program.Entitles,
            g.SourceRef,
            ExpiresOn = (g.ExpiresOn ?? g.Program.GrantsExpireOn)?.ToInvariantDate(),
            IssuedAt = g.IssuedAt.ToIso8601(),
            NotifiedAt = g.NotifiedAt.ToIso8601(),
            RevokedAt = g.RevokedAt.ToIso8601(),
            g.RevokeReason
        }).ToList();

        var decisions = new
        {
            ProgramsCreated = rows.ProgramsCreated.Select(p => new
            {
                p.Name,
                Year = YearOf(p, events),
                CreatedAt = p.CreatedAt.ToIso8601()
            }).ToList(),
            ManagerOf = rows.ManagerRows.Select(m => new { Program = m.Program.Name, AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            ManagersNamed = rows.ManagersAdded.Select(m => new { Program = m.Program.Name, AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            GrantsIssued = rows.GrantsIssued.Select(g => new { Program = g.Program.Name, IssuedAt = g.IssuedAt.ToIso8601() }).ToList(),
            GrantsRevoked = rows.GrantsRevoked.Select(g => new { Program = g.Program.Name, RevokedAt = g.RevokedAt.ToIso8601() }).ToList()
        };
        var hasDecisions = decisions.ProgramsCreated.Count + decisions.ManagerOf.Count + decisions.ManagersNamed.Count
            + decisions.GrantsIssued.Count + decisions.GrantsRevoked.Count > 0;

        return
        [
            new UserDataSlice(Grants, grants),
            new UserDataSlice(GrantDecisions, hasDecisions ? decisions : null)
        ];
    }

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repository.EraseUserAsync(userId, ct);

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var revoked = await repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct);
        foreach (var grantId in revoked)
            await auditLog.LogAsync(GrantsAuditActions.GrantRevoked, AuditEntityTypes.Grant, grantId,
                $"Duplicate grant revoked on merge of {mergedFromUserId} into {mergedToUserId}.",
                actorUserId, relatedEntityId: mergedToUserId, relatedEntityType: AuditSubjectTypes.User);
    }
}
