using System.Globalization;
using Humans.Email.Contracts;
using Humans.Grants.Data;
using Humans.Grants.Domain;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>Nothing runs on a timer: expiry is a date compared when read.</summary>
internal sealed partial class GrantService(
    IGrantRepository repository,
    IUserService users,
    IUserEmailService userEmails,
    INotificationEmitter notifications,
    IEmailService email,
    GrantsEmails emailFactory,
    IAuditLogService auditLog,
    ISettingsService settingsService,
    IClock clock,
    ILogger<GrantService> logger) : IGrantManagement
{
    internal static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

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
            p.Status, p.CreatedByUserId, p.CreatedAt, p.UpdatedAt,
            p.Managers.Select(m => m.UserId).ToList(),
            p.Grants.Select(g => ToInfo(g, p, evt)).ToList());
    }

    /// <summary>The program's own event, else the one <c>grants.event-ids</c> would give it.</summary>
    private static EventSettingsInfo? EventOf(GrantProgram p, IReadOnlyList<EventSettingsInfo> events) =>
        p.EventSettingsId is { } id ? events.FirstOrDefault(e => e.Id == id) : PickEventForYear(events, p.Year);

    /// <summary>The earliest non-deleted event of <paramref name="year"/> by gate opening; null when it has none.</summary>
    internal static EventSettingsInfo? PickEventForYear(IReadOnlyList<EventSettingsInfo> events, int year) =>
        events
            .Where(e => e.Year == year && e.Status != EventSettingsStatus.Deleted)
            .OrderBy(e => e.GateOpeningDate)
            .FirstOrDefault();

    public async Task<IReadOnlyList<(Guid ProgramId, string ProgramName, int Year, EventSettingsInfo Event)>> GetEventIdPlanAsync(
        CancellationToken ct = default)
    {
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        return [.. (await repository.GetProgramsAsync(ct))
            .Where(p => p.EventSettingsId is null)
            .Select(p => (p, Event: PickEventForYear(events, p.Year)))
            .Where(x => x.Event is not null)
            .Select(x => (x.p.Id, x.p.Name, x.p.Year, x.Event!))];
    }

    public async Task<int> AssignProgramEventIdsAsync(CancellationToken ct = default) =>
        await repository.AssignEventIdsAsync(
            (await GetEventIdPlanAsync(ct)).ToDictionary(p => p.ProgramId, p => p.Event.Id), ct);

    /// <summary>The year comes from the event, which a gate-opening change can move to another year.</summary>
    private static GrantInfo ToInfo(Grant g, GrantProgram p, EventSettingsInfo? evt) => new(
        g.Id, g.ProgramId, p.Name, p.Entitles, evt?.Year ?? p.Year, g.UserId, g.SourceRef, g.ExpiresOn,
        g.ExpiresOn ?? p.GrantsExpireOn, g.IssuedAt, g.IssuedByUserId, g.NotifiedAt, g.RevokedAt,
        g.RevokedByUserId, g.RevokeReason);

    /// <summary>The survivor id first, followed by every merged id, so writes and holding checks identify one person.</summary>
    private async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

    private static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private Task AuditGrantAsync(AuditActionDefinition action, Grant grant, string description, Guid actorUserId) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: AuditEntityTypes.User);

    private Task AuditProgramAsync(
        AuditActionDefinition action, GrantProgram program, string description, Guid actorUserId, Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditEntityTypes.User);
}
