using Humans.Grants.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Grants.Domain;
using NodaTime;

namespace Humans.Grants.Services;

internal sealed partial class GrantService
{
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
            recipients.Add(await AllIdsAsync(id, ct));
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
        Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return new("Grant not found.");
        if (grant.RevokedAt is not null) return new("The grant is already revoked.");
        if ((reason?.Length ?? 0) > ReasonMax) return new("The reason is too long.");

        await RevokeCoreAsync(grant, Trimmed(reason), actorUserId, ct);
        return GrantMutationResult.Ok;
    }

    public async Task<GrantMutationResult> ResendNoticeAsync(Guid grantId, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return new("Grant not found.");
        if (grant.RevokedAt is not null) return new("A revoked grant has no notice to resend.");

        var year = EventOf(grant.Program, await settingsService.GetAllEventSettingsAsync(ct))?.Year ?? grant.Program.Year;
        grant.NotifiedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);
        await AuditGrantAsync(AuditAction.GrantNoticeResent, grant,
            $"Notice for \"{grant.Program.Name}\" ({year}) sent again.", actorUserId);
        await AnnounceIssuedAsync(grant, grant.Program, CancellationToken.None);
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

        var personIds = await AllIdsAsync(userId, ct);
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
        var year = EventOf(program, await settingsService.GetAllEventSettingsAsync(ct))?.Year ?? program.Year;
        await repository.AddGrantsAsync(grants, ct);
        // The grants committed; finish their audit and notices after the browser disconnects.
        foreach (var grant in grants)
        {
            await AuditGrantAsync(AuditAction.GrantIssued, grant, describe(grant, year), actorUserId);
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

    private static bool IsOpenSource(GrantProgram program, string sourceKey) =>
        program.IssuedBy == GrantIssuer.Source && program.Status == GrantProgramStatus.Open
        && string.Equals(program.SourceKey, sourceKey, StringComparison.Ordinal);

    private async Task RevokeCoreAsync(Grant grant, string? reason, Guid actorUserId, CancellationToken ct)
    {
        var year = EventOf(grant.Program, await settingsService.GetAllEventSettingsAsync(ct))?.Year ?? grant.Program.Year;
        grant.RevokedAt = clock.GetCurrentInstant();
        grant.RevokedByUserId = actorUserId;
        grant.RevokeReason = reason;
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        await AuditGrantAsync(AuditAction.GrantRevoked, grant,
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
}
