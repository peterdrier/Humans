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
    INotificationEmitter notifications,
    IEmailService email,
    GrantsEmails emailFactory,
    IAuditLogService auditLog,
    ISettingsService settingsService,
    IClock clock,
    ILogger<GrantService> logger) : IGrantManagement
{
    private readonly GrantReader reader = new(repository, users, settingsService);
    private readonly GrantNotices notices = new(users, notifications, email, emailFactory, logger);

    internal static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        reader.GetProgramsAsync(ct);

    public Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default) =>
        reader.GetProgramAsync(programId, ct);

    public Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetGrantsForUserAsync(userId, ct);

    public Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        reader.ManagesAnyProgramAsync(userId, ct);

    private async Task<IReadOnlyList<Guid>?> ResolveAvailablePersonIdsAsync(Guid userId, CancellationToken ct)
    {
        var person = await users.GetUserInfoAsync(userId, ct);
        if (person is not null && !person.IsTombstone) return person.AllUserIds;

        logger.LogWarning("Grant write refused for unavailable person {UserId}", userId);
        return null;
    }

    private static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private Task AuditGrantAsync(AuditActionDefinition action, Grant grant, string description, Guid actorUserId) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: grant.UserId, relatedEntityType: AuditSubjectTypes.User);

    private Task AuditProgramAsync(
        AuditActionDefinition action, GrantProgram program, string description, Guid actorUserId, Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditSubjectTypes.User);
}
