using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Grants.Data;
using Humans.Grants.Domain;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;

namespace Humans.Grants.Services;

internal sealed class GrantReader(
    IGrantRepository repository,
    IUserService users,
    ISettingsService settingsService) : IApplicationService
{
    internal async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default)
    {
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        return (await repository.GetProgramsAsync(ct)).Select(p => ToInfo(p, events)).ToList();
    }

    internal async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program, await settingsService.GetAllEventSettingsAsync(ct));
    }

    internal async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct);
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        return grants.Select(g => ToInfo(g, g.Program, EventOf(g.Program, events))).ToList();
    }

    internal async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
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

}
