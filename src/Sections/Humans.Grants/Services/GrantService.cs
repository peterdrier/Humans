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

namespace Humans.Grants.Services;

/// <summary>Nothing runs on a timer: expiry is a date compared when read.</summary>
internal sealed partial class GrantService(
    IOrgTimeZone orgTimeZone,
    IGrantRepository repository,
    IUserService users,
    INotificationEmitter notifications,
    IEmailService email,
    GrantsEmails emailFactory,
    IAuditLogService auditLog,
    ISettingsService settingsService,
    IClock clock,
    ILogger<GrantService> logger) : IGrantManagement
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
        Grant grant, GrantProgram program, GrantNoticeKind kind, NotificationSourceDefinition source, CancellationToken ct)
    {
        // Each step is best-effort on its own: one channel's outage never suppresses the other.
        UserInfo? person = null;
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
            if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email))
            {
                logger.LogWarning("Skipping grant {Kind} email for {UserId} on grant {GrantId}: no effective email",
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
        NodaTime.LocalDate? expiresOn, string? reason, CancellationToken ct)
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
}
