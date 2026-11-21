using Humans.Grants.Contracts;

namespace Humans.Grants.Services;

internal sealed partial class GrantService : IGrantService
{
    public async Task<IReadOnlyList<ActiveGrantInfo>> GetActiveGrantsAsync(Guid userId, CancellationToken ct = default)
    {
        var grants = await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct);
        var events = await settingsService.GetAllEventSettingsAsync(ct);
        var today = Today;
        return grants
            .Select(g => (Grant: g, Info: ToInfo(g, g.Program, EventOf(g.Program, events))))
            .Where(x => x.Info.State(today) == GrantState.Active)
            .Select(x => new ActiveGrantInfo(x.Grant.Id, x.Grant.ProgramId, x.Grant.Program.Name, x.Grant.Program.EventSettingsId))
            .ToList();
    }

    public async Task<IReadOnlyList<GrantProgramSummary>> GetProgramSummariesAsync(CancellationToken ct = default) =>
        (await GetProgramsAsync(ct))
            .OrderByDescending(p => p.Year).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new GrantProgramSummary(p.Id, p.Name, p.Year, p.EventSettingsId, p.EventName))
            .ToList();
}
