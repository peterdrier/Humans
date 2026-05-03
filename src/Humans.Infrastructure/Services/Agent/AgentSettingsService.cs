using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentSettingsService : IAgentSettingsService
{
    private readonly IAgentRepository _repo;
    private readonly IAgentSettingsStore _store;
    private readonly IClock _clock;

    public AgentSettingsService(IAgentRepository repo, IAgentSettingsStore store, IClock clock)
    {
        _repo = repo;
        _store = store;
        _clock = clock;
    }

    public AgentSettings Current => _store.Current;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var row = await _repo.GetSettingsAsync(cancellationToken);
        if (row is not null)
            _store.Set(row);
    }

    public async Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken)
    {
        var row = await _repo.UpdateSettingsAsync(mutator, _clock.GetCurrentInstant(), cancellationToken);
        _store.Set(row);
    }
}
