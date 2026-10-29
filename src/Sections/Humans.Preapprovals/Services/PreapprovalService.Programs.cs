using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;

namespace Humans.Preapprovals.Services;

internal sealed partial class PreapprovalService
{
    private const int NameMax = 200;
    private const int DescriptionMax = 2000;

    public async Task<PreapprovalMutationResult<Guid>> CreateProgramAsync(
        ProgramSave save, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (Validate(save, existing: null) is { } refusal) return new(Refusal: refusal);
        var account = await ResolveAccountAsync(save.HoldedAccountNumber, keep: null, ct);
        if (account is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.AccountNotFound));

        var now = clock.GetCurrentInstant();
        var program = new PreapprovalProgram
        {
            Id = Guid.NewGuid(),
            Name = save.Name.Trim(),
            Description = Trimmed(save.Description),
            Kind = save.Kind,
            PerPersonAmount = save.Kind == ProgramKind.FixedPerPerson ? save.PerPersonAmount : null,
            TotalCeiling = save.TotalCeiling,
            HoldedAccountNumber = save.HoldedAccountNumber,
            HoldedAccountId = account,
            AcceptsApplications = save.AcceptsApplications,
            ClaimBy = save.ClaimBy,
            Status = save.Status,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var userId in save.ManagerUserIds.Distinct())
            program.Managers.Add(new ProgramManager
            { ProgramId = program.Id, UserId = userId, AddedByUserId = actorUserId, AddedAt = now });
        await repository.AddProgramAsync(program, ct);

        await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramCreated, program,
            $"Program \"{program.Name}\" created: {program.Kind}, " +
            $"{(program.PerPersonAmount is { } per ? $"{Euros(per)} per person, " : "")}" +
            $"ceiling {(program.TotalCeiling is { } c ? Euros(c) : "none")}, account {program.HoldedAccountNumber}, " +
            $"claim by {program.ClaimBy.ToInvariantDate()}, {program.Status}.",
            actorUserId);
        foreach (var manager in program.Managers)
            await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded, program,
                $"Manager added to \"{program.Name}\".", actorUserId, relatedUserId: manager.UserId);

        return new(program.Id);
    }

    public async Task<PreapprovalMutationResult> UpdateProgramAsync(
        Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (Validate(save, program) is { } refusal) return new(refusal);

        var account = await ResolveAccountAsync(save.HoldedAccountNumber, keep: program, ct);
        if (account is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.AccountNotFound));

        var info = ToInfo(program);
        if (save.TotalCeiling is { } ceiling && ceiling < info.Granted)
            return new(new PreapprovalRefusal(PreapprovalErrorKeys.CeilingBelowCommitted, Euros(info.Granted)));

        var now = clock.GetCurrentInstant();
        var changes = new List<string>();
        var oldClaimBy = program.ClaimBy;
        var wasOpen = program.Status == ProgramStatus.Open;

        Change(changes, "name", program.Name, save.Name.Trim(), v => program.Name = v);
        Change(changes, "description", program.Description, Trimmed(save.Description), v => program.Description = v);
        var perPerson = save.Kind == ProgramKind.FixedPerPerson ? save.PerPersonAmount : null;
        Change(changes, "per-person amount", program.PerPersonAmount, perPerson, v => program.PerPersonAmount = v, Money);
        Change(changes, "ceiling", program.TotalCeiling, save.TotalCeiling, v => program.TotalCeiling = v, Money);
        Change(changes, "account", program.HoldedAccountNumber, save.HoldedAccountNumber, v => program.HoldedAccountNumber = v);
        program.HoldedAccountId = account;
        Change(changes, "accepts applications", program.AcceptsApplications, save.AcceptsApplications, v => program.AcceptsApplications = v);
        Change(changes, "claim by", program.ClaimBy, save.ClaimBy, v => program.ClaimBy = v, d => d.ToInvariantDate());
        Change(changes, "status", program.Status, save.Status, v => program.Status = v);
        Change(changes, "kind", program.Kind, save.Kind, v => program.Kind = v);
        program.UpdatedAt = now;
        await repository.UpdateProgramAsync(program, ct);
        // The program committed; finish manager changes, audit and deadline notices independently of the browser.
        ct = CancellationToken.None;

        var wanted = save.ManagerUserIds.Distinct().ToList();
        var current = program.Managers.Select(m => m.UserId).ToList();
        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        if (added.Count > 0 || removed.Count > 0)
            await repository.SetManagersAsync(program.Id,
                added.Select(id => new ProgramManager { UserId = id, AddedByUserId = actorUserId, AddedAt = now }).ToList(),
                removed, ct);

        if (changes.Count > 0)
            await AuditProgramAsync(
                wasOpen && program.Status == ProgramStatus.Closed
                    ? PreapprovalsAuditActions.PreapprovalProgramClosed
                    : PreapprovalsAuditActions.PreapprovalProgramEdited,
                program, $"Program \"{program.Name}\" edited: {string.Join("; ", changes)}.", actorUserId);
        foreach (var id in added)
            await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded, program,
                $"Manager added to \"{program.Name}\".", actorUserId, relatedUserId: id);
        foreach (var id in removed)
            await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramManagerRemoved, program,
                $"Manager removed from \"{program.Name}\".", actorUserId, relatedUserId: id);

        if (program.ClaimBy != oldClaimBy)
            await DeadlineMovedAsync(program, actorUserId, ct);

        return PreapprovalMutationResult.Ok;
    }

    /// <summary>
    /// Reminder markers carry the deadline they announced, so the reminders simply run again for the new date.
    /// </summary>
    private async Task DeadlineMovedAsync(PreapprovalProgram program, Guid actorUserId, CancellationToken ct)
    {
        foreach (var grant in program.Grants.Where(g => g.Status == GrantStatus.Open))
        {
            await ResolveFileByAsync(grant.Id, actorUserId, ct);
            var info = ToInfo(grant, program);
            await NotifyFileByAsync(info, "Preapprovals_Notice_DeadlineChanged", NotificationPriority.High, ct);
            await EmailAsync(info, PreapprovalNoticeKind.DeadlineChanged, detail: null, ct);
        }
    }

    private static PreapprovalRefusal? Validate(ProgramSave save, PreapprovalProgram? existing)
    {
        if (string.IsNullOrWhiteSpace(save.Name)) return new(PreapprovalErrorKeys.NameRequired);
        if (save.Name.Trim().Length > NameMax || (save.Description?.Length ?? 0) > DescriptionMax)
            return new(PreapprovalErrorKeys.TextTooLong);
        if (save.Kind == ProgramKind.FixedPerPerson)
        {
            if (save.PerPersonAmount is not { } per) return new(PreapprovalErrorKeys.PerPersonAmountRequired);
            if (per <= 0) return new(PreapprovalErrorKeys.AmountPositive);
        }
        else if (save.PerPersonAmount is not null)
        {
            return new(PreapprovalErrorKeys.PerPersonAmountNotAllowed);
        }
        if (save.TotalCeiling is { } ceiling && ceiling <= 0) return new(PreapprovalErrorKeys.AmountPositive);
        // The kind decides how every grant is sized; once one exists it cannot change under them.
        if (existing is { Grants.Count: > 0 } && existing.Kind != save.Kind)
            return new(PreapprovalErrorKeys.KindLocked);
        return null;
    }

    /// <summary>An unchanged number keeps its id, so an account retired since never blocks a deadline edit.</summary>
    private async Task<string?> ResolveAccountAsync(int accountNumber, PreapprovalProgram? keep, CancellationToken ct)
    {
        if (keep is not null && keep.HoldedAccountNumber == accountNumber) return keep.HoldedAccountId;
        var option = (await finance.ListExpenseAccountsAsync(activeOnly: true, ct))
            .FirstOrDefault(a => a.AccountNum == accountNumber);
        return option?.AccountId;
    }

    private static void Change<T>(
        List<string> changes, string field, T before, T after, Action<T> apply, Func<T, string>? show = null)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        show ??= v => v?.ToString() ?? "none";
        changes.Add($"{field} {show(before)} → {show(after)}");
        apply(after);
    }

    private static string Money(decimal? value) => value is { } v ? Euros(v) : "none";

    private static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
