using System.Collections.Concurrent;
using Humans.Shifts.Domain;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Shifts.Tests.Infrastructure;

/// <summary>
/// A real <see cref="CachingShiftViewService"/> over a stub inner, so service tests assert
/// eviction on the cache itself rather than on a substitute. Every read answers with an
/// empty view unless a test primed one; <see cref="IsRotaCachedAsync"/> reports whether a read was served
/// without the inner.
/// </summary>
internal sealed class ShiftViewProbe
{
    private readonly ConcurrentDictionary<Guid, int> _rotaLoads = new();
    private readonly ConcurrentDictionary<Guid, ShiftRotaView> _rotas = new();

    public ShiftViewProbe()
    {
        var inner = Substitute.For<IShiftRowView>();
        inner.GetUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<ShiftUserView>(LoadUser(ci.Arg<Guid>())));
        inner.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>>(
                ci.Arg<IEnumerable<Guid>>().Distinct().ToDictionary(id => id, LoadUser)));
        inner.GetRotaAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<ShiftRotaView>(LoadRota(ci.Arg<Guid>())));

        var services = new ServiceCollection();
        services.AddKeyedScoped<IShiftRowView>(CachingShiftViewService.InnerServiceKey, (_, _) => inner);
        Cache = new CachingShiftViewService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingShiftViewService>.Instance);
    }

    public CachingShiftViewService Cache { get; }

    /// <summary>Caches a view for <paramref name="rotaId"/> holding each shift.</summary>
    public async Task PrimeRotaAsync(Guid rotaId, params Shift[] shifts)
    {
        _rotas[rotaId] = new ShiftRotaView(rotaId, null, shifts, [], []);
        await Cache.GetRotaAsync(rotaId);
    }

    public async Task<bool> IsRotaCachedAsync(Guid rotaId)
    {
        var before = _rotaLoads.GetValueOrDefault(rotaId);
        await Cache.GetRotaAsync(rotaId);
        return _rotaLoads.GetValueOrDefault(rotaId) == before;
    }

    /// <summary>Entries removed or bulk clears on the rota cache since construction.</summary>
    public long Evictions =>
        Cache.RotaCacheStats.KeyRemovals + Cache.RotaCacheStats.BulkInvalidations;

    private static ShiftUserView LoadUser(Guid userId) => new(userId, null, null, null, [], []);

    private ShiftRotaView LoadRota(Guid rotaId)
    {
        _rotaLoads.AddOrUpdate(rotaId, 1, (_, n) => n + 1);
        return _rotas.TryGetValue(rotaId, out var view)
            ? view
            : new ShiftRotaView(rotaId, null, [], [], []);
    }
}
