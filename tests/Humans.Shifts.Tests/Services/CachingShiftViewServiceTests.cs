using Humans.Shifts.Domain;
using Humans.Shifts.Services;
using AwesomeAssertions;
using Humans.Shifts.Services.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Shifts.Tests.Services;

public class CachingShiftViewServiceTests
{
    private readonly IShiftRowView _inner = Substitute.For<IShiftRowView>();

    private CachingShiftViewService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IShiftRowView>(
            CachingShiftViewService.InnerServiceKey, (_, _) => _inner);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new CachingShiftViewService(
            scopeFactory,
            NullLogger<CachingShiftViewService>.Instance);
    }

    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task RotaLoadStartedBeforeInvalidation_DoesNotRepopulateCache(int invalidation)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var rotaId = Guid.NewGuid();
        var shift = new Shift { Id = Guid.NewGuid(), RotaId = rotaId };
        var old = new ShiftRotaView(rotaId, null, [shift], [], []);
        var current = old with { Shifts = [] };
        var pending = new TaskCompletionSource<ShiftRotaView>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inner.GetRotaAsync(rotaId, ct).Returns(_ => new ValueTask<ShiftRotaView>(pending.Task));
        var sut = CreateSut();
        var read = sut.GetRotaAsync(rotaId, ct).AsTask();

        switch (invalidation)
        {
            case 0: sut.InvalidateRota(rotaId); break;
            case 1: sut.InvalidateShift(shift.Id); break;
            default: sut.InvalidateAll(); break;
        }
        pending.SetResult(old);
        (await read).Should().BeSameAs(old);
        _inner.GetRotaAsync(rotaId, ct).Returns(new ValueTask<ShiftRotaView>(current));

        (await sut.GetRotaAsync(rotaId, ct)).Should().BeSameAs(current);
    }

    [HumansFact]
    public async Task GetUserAsync_IsNotCached_EveryReadGoesToInner()
    {
        var userId = Guid.NewGuid();
        var view = new ShiftUserView(userId, null, null, null, [], []);
        _inner.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>>(
                new Dictionary<Guid, ShiftUserView> { [userId] = view }));
        var sut = CreateSut();

        (await sut.GetUserAsync(userId, Xunit.TestContext.Current.CancellationToken)).Should().BeSameAs(view);
        (await sut.GetUserAsync(userId, Xunit.TestContext.Current.CancellationToken)).Should().BeSameAs(view);

        await _inner.Received(2).GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRotaAsync_DictMiss_DelegatesToInner_AndCachesResult()
    {
        var rotaId = Guid.NewGuid();
        var view = new ShiftRotaView(
            rotaId, Rota: null,
            Shifts: [],
            Tags: [],
            Signups: []);
        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(view));

        var sut = CreateSut();

        (await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken)).Should().BeSameAs(view);
        (await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken)).Should().BeSameAs(view);

        await _inner.Received(1).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAll_ClearsRotaCache()
    {
        var rotaId = Guid.NewGuid();
        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(new ShiftRotaView(
                rotaId, null, [], [], [])));

        var sut = CreateSut();
        await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

        sut.InvalidateAll();

        await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

        await _inner.Received(2).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EventSettingsChanged_ClearsRotaCache()
    {
        var rotaId = Guid.NewGuid();
        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(new ShiftRotaView(
                rotaId, null, [], [], [])));

        var sut = CreateSut();
        await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

        (sut).EventSettingsChanged(Guid.NewGuid());

        await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

        await _inner.Received(2).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateShift_EvictsAffectedRota()
    {
        var shiftId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        var rotaView = new ShiftRotaView(
            rotaId, Rota: null,
            Shifts: [new Shift { Id = shiftId, RotaId = rotaId }],
            Tags: [],
            Signups: []);
        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(rotaView));
        var sut = CreateSut();
        await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

        sut.InvalidateShift(shiftId);
        await sut.GetRotaAsync(rotaId, Xunit.TestContext.Current.CancellationToken);

        await _inner.Received(2).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void EventSettingsChanged_ForwardsChangedEventToInner()
    {
        // The coordinator-dashboard aggregates sit on a sliding TTL and classify shifts off the
        // event's Settings calendar, so a gate-date or offset edit must evict them for that
        // event; a regularly viewed dashboard never expires on its own.
        var eventSettingsId = Guid.NewGuid();

        (CreateSut()).EventSettingsChanged(eventSettingsId);

        _inner.Received(1).EventSettingsChanged(eventSettingsId);
    }
}
