using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Grants.Domain;
using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

internal sealed partial class GrantService
{
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
        foreach (var userId in save.ManagerUserIds.Distinct())
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
        await repository.UpdateProgramAsync(program, ct);
        // The program committed; finish manager changes and audit independently of the browser.
        ct = CancellationToken.None;

        var wanted = save.ManagerUserIds.Distinct().ToList();
        var current = program.Managers.Select(m => m.UserId).ToList();
        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        if (added.Count > 0 || removed.Count > 0)
            await repository.SetManagersAsync(program.Id,
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

    private static string? Validate(ProgramSave save)
    {
        if (string.IsNullOrWhiteSpace(save.Name)) return "A name is required.";
        if (string.IsNullOrWhiteSpace(save.Entitles)) return "Say what a grant entitles the person to.";
        if (save.Name.Trim().Length > NameMax || save.Entitles.Trim().Length > EntitlesMax
            || (save.Description?.Length ?? 0) > DescriptionMax)
            return "A field is too long.";
        if (!Enum.IsDefined(save.IssuedBy) || !Enum.IsDefined(save.Status)) return "The issuer or status is out of range.";
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
}
