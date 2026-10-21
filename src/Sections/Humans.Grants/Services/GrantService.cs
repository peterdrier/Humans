using Humans.Email.Contracts;
using Humans.Grants.Data;
using Humans.Grants.Domain;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>
/// The grant rules: programs and their managers, issuing, revoking and resending, a source
/// section filling its program, and the GDPR surface. Every change is a person's act, audited
/// with the actor; nothing runs on a timer, and expiry is a date compared when read.
/// </summary>
/// <remarks>
/// EF-free: the repository hands over detached entities and takes them back. "Today" is the
/// Europe/Madrid date, the zone the association works in.
/// </remarks>
internal sealed partial class GrantService(
    IGrantRepository repository,
    IUserService users,
    IUserEmailService userEmails,
    INotificationEmitter notifications,
    IEmailService email,
    GrantsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<GrantService> logger) : IGrantManagement
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    // ── Reads ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        (await repository.GetProgramsAsync(ct)).Select(ToInfo).ToList();

    public async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program);
    }

    public async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct)).Select(ToInfo).ToList();

    public async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        await repository.ManagesAnyProgramAsync(await AllIdsAsync(userId, ct), ct);

    // ── Mapping ───────────────────────────────────────────────────────────

    private static ProgramInfo ToInfo(GrantProgram p) => new(
        p.Id, p.Name, p.Entitles, p.Description, p.Year, p.IssuedBy, p.SourceKey, p.GrantsExpireOn,
        p.Status, p.CreatedByUserId, p.CreatedAt, p.UpdatedAt,
        p.Managers.Select(m => m.UserId).ToList(),
        p.Grants.OrderBy(g => g.IssuedAt).Select(g => ToInfo(g, p)).ToList());

    private static GrantInfo ToInfo(Grant g) => ToInfo(g, g.Program);

    private static GrantInfo ToInfo(Grant g, GrantProgram p) => new(
        g.Id, g.ProgramId, p.Name, p.Entitles, p.Year, g.UserId, g.SourceRef, g.ExpiresOn,
        g.ExpiresOn ?? p.GrantsExpireOn, g.IssuedAt, g.IssuedByUserId, g.NotifiedAt, g.RevokedAt,
        g.RevokedByUserId, g.RevokeReason);

    /// <summary>Every id the person has held, so a grant under a since-merged id is still theirs.</summary>
    private async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

    private static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private Task AuditGrantAsync(AuditAction action, Grant grant, string description, Guid actorUserId) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: AuditEntityTypes.User);

    private Task AuditProgramAsync(
        AuditAction action, GrantProgram program, string description, Guid actorUserId, Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditEntityTypes.User);
}
