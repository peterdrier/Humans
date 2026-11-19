using System.Security.Cryptography;
using System.Text;
using Humans.Base.Demo;
using Humans.Development.Data;
using Humans.Development.Domain;
using NodaTime;

namespace Humans.Development.Services.Demo;

internal sealed class DemoWorld(
    int generation,
    Guid actorUserId,
    IReadOnlyList<DemoLedgerEntry> entries,
    IDemoLedgerRepository repository,
    IClock clock,
    List<string> report) : IDemoWorld
{
    private readonly Dictionary<Guid, DemoLedgerEntry> _entries = entries.ToDictionary(e => e.Id);

    public int Generation { get; } = generation;

    public Guid ActorUserId { get; } = actorUserId;

    public async Task<Guid> EnsureAsync(
        string key,
        Func<Guid, CancellationToken, Task<bool>> exists,
        Func<Guid, CancellationToken, Task<Guid>> create,
        CancellationToken ct)
    {
        var id = LedgerId(key, Generation);
        _entries.TryGetValue(id, out var entry);

        switch (entry?.State)
        {
            case DemoLedgerState.LeftBehind or DemoLedgerState.Retired:
                return entry.EntityId;
            case DemoLedgerState.Made when await exists(entry.EntityId, ct):
                return entry.EntityId;
            case DemoLedgerState.Pending when await exists(entry.EntityId, ct):
                await SaveAsync(entry, DemoLedgerState.Made, entry.EntityId, null, ct);
                return entry.EntityId;
            case DemoLedgerState.Made:
                Report($"{key}: was gone, made again");
                break;
        }

        // A made-then-vanished row gets a fresh id: the old one may now belong to a tombstone.
        var planned = entry?.State == DemoLedgerState.Pending ? entry.EntityId : Guid.NewGuid();
        var now = clock.GetCurrentInstant();
        entry ??= new DemoLedgerEntry { Id = id, Key = key, Generation = Generation, CreatedAt = now };
        await SaveAsync(entry, DemoLedgerState.Pending, planned, null, ct);

        var made = await create(planned, ct);
        await SaveAsync(entry, DemoLedgerState.Made, made, null, ct);
        return made;
    }

    public Task<IReadOnlyDictionary<string, Guid>> MadeAsync(string keyPrefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(_entries.Values
            .Where(e => e.Generation == Generation && e.State == DemoLedgerState.Made
                && e.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.EntityId, StringComparer.Ordinal));

    public Task MarkRetiredAsync(string key, CancellationToken ct) =>
        MarkAsync(key, DemoLedgerState.Retired, null, ct);

    public Task MarkLeftBehindAsync(string key, string reason, CancellationToken ct)
    {
        Report($"{key}: left behind ({reason})");
        return MarkAsync(key, DemoLedgerState.LeftBehind, reason, ct);
    }

    public void Report(string line) => report.Add(line);

    internal static Guid LedgerId(string key, int generation)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"demo-ledger:{generation}:{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task MarkAsync(string key, DemoLedgerState state, string? note, CancellationToken ct)
    {
        if (!_entries.TryGetValue(LedgerId(key, Generation), out var entry))
            throw new InvalidOperationException($"Demo ledger has no '{key}' in generation {Generation}.");
        await SaveAsync(entry, state, entry.EntityId, note, ct);
    }

    private async Task SaveAsync(
        DemoLedgerEntry entry, DemoLedgerState state, Guid entityId, string? note, CancellationToken ct)
    {
        entry.State = state;
        entry.EntityId = entityId;
        entry.Note = note;
        entry.UpdatedAt = clock.GetCurrentInstant();
        await repository.SaveAsync(entry, ct);
        _entries[entry.Id] = entry;
    }
}
