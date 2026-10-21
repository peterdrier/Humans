using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Grants.Domain;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>
/// Issuing, revoking and resending, as managers drive them and as a source section does
/// through <c>IGrantWrite</c>. Every change is audited; every issue and revoke tells the person.
/// </summary>
internal sealed partial class GrantService
{
    private const int ReasonMax = 500;
    private const int SourceRefMax = 128;

    // ── Managers ──────────────────────────────────────────────────────────

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

        var holding = program.Grants.Where(g => g.RevokedAt is null).Select(g => g.UserId).ToHashSet();
        var now = clock.GetCurrentInstant();
        var grants = ids.Where(id => !holding.Contains(id))
            .Select(id => NewGrant(program, id, sourceRef: null, expiresOn, actorUserId, now))
            .ToList();
        if (grants.Count == 0) return new(Value: []);

        await repository.AddGrantsAsync(grants, ct);
        // The grants committed; finish their audit and notices after the browser disconnects.
        ct = CancellationToken.None;
        foreach (var grant in grants)
        {
            await AuditGrantAsync(AuditAction.GrantIssued, grant,
                $"Issued under \"{program.Name}\" ({program.Year}){ExpirySuffix(grant, program)}" +
                $"{(grants.Count > 1 ? $" (batch of {grants.Count})" : "")}.",
                actorUserId);
            await AnnounceIssuedAsync(grant, program, ct);
        }
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

        grant.NotifiedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);
        await AuditGrantAsync(AuditAction.GrantNoticeResent, grant,
            $"Notice for \"{grant.Program.Name}\" ({grant.Program.Year}) sent again.", actorUserId);
        await AnnounceIssuedAsync(grant, grant.Program, CancellationToken.None);
        return GrantMutationResult.Ok;
    }

    // ── IGrantWrite: a source section ─────────────────────────────────────

    public async Task<Guid> IssueAsync(
        string sourceKey, Guid userId, string? sourceRef, Guid actorUserId, CancellationToken ct = default)
    {
        sourceRef = Trimmed(sourceRef);
        if (sourceRef?.Length > SourceRefMax)
            throw new ArgumentException($"Source reference longer than {SourceRefMax} characters.", nameof(sourceRef));
        var program = await SourceProgramAsync(sourceKey, ct);

        var live = program.Grants.FirstOrDefault(g => g.UserId == userId && g.RevokedAt is null);
        if (live is not null) return live.Id;

        var grant = NewGrant(program, userId, sourceRef, expiresOn: null, actorUserId, clock.GetCurrentInstant());
        await repository.AddGrantsAsync([grant], ct);
        ct = CancellationToken.None;
        await AuditGrantAsync(AuditAction.GrantIssued, grant,
            $"Issued under \"{program.Name}\" ({program.Year}) by source {program.SourceKey}" +
            $"{(sourceRef is null ? "" : $", ref {sourceRef}")}{ExpirySuffix(grant, program)}.",
            actorUserId);
        await AnnounceIssuedAsync(grant, program, ct);
        return grant.Id;
    }

    public async Task<bool> RevokeAsync(
        string sourceKey, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        if ((reason?.Length ?? 0) > ReasonMax)
            throw new ArgumentException($"Reason longer than {ReasonMax} characters.", nameof(reason));
        var program = await SourceProgramAsync(sourceKey, ct);
        var live = program.Grants.FirstOrDefault(g => g.UserId == userId && g.RevokedAt is null);
        if (live is null) return false;
        live.Program = program;
        await RevokeCoreAsync(live, Trimmed(reason), actorUserId, ct);
        return true;
    }

    /// <summary>The one open program filled by <paramref name="sourceKey"/>, or a refusal.</summary>
    private async Task<GrantProgram> SourceProgramAsync(string sourceKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return (await repository.GetProgramsAsync(ct)).SingleOrDefault(p => IsOpenSource(p, sourceKey))
            ?? throw new InvalidOperationException($"No open grant program is filled by source {sourceKey}.");
    }

    private static bool IsOpenSource(GrantProgram program, string sourceKey) =>
        program.IssuedBy == GrantIssuer.Source && program.Status == GrantProgramStatus.Open
        && string.Equals(program.SourceKey, sourceKey, StringComparison.Ordinal);

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task RevokeCoreAsync(Grant grant, string? reason, Guid actorUserId, CancellationToken ct)
    {
        grant.RevokedAt = clock.GetCurrentInstant();
        grant.RevokedByUserId = actorUserId;
        grant.RevokeReason = reason;
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        await AuditGrantAsync(AuditAction.GrantRevoked, grant,
            $"Revoked under \"{grant.Program.Name}\" ({grant.Program.Year}){(reason is null ? "" : $": {reason}")}.",
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
