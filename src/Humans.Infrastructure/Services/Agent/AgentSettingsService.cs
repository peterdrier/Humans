using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentSettingsService : IAgentSettingsService
{
    private readonly HumansDbContext _db;
    private readonly IAgentSettingsStore _store;
    private readonly IClock _clock;

    public AgentSettingsService(HumansDbContext db, IAgentSettingsStore store, IClock clock)
    {
        _db = db;
        _store = store;
        _clock = clock;
    }

    public AgentSettings Current => _store.Current;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var row = await _db.AgentSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);
        if (row is not null)
            _store.Set(row);
    }

    public async Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken)
    {
        var row = await _db.AgentSettings.FirstAsync(s => s.Id == 1, cancellationToken);
        mutator(row);
        row.UpdatedAt = _clock.GetCurrentInstant();
        await _db.SaveChangesAsync(cancellationToken);
        _store.Set(row);
    }
}
