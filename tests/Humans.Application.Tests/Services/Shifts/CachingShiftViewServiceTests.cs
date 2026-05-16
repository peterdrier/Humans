using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Shifts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Tests for <see cref="CachingShiftViewService"/> — Singleton decorator over a
/// keyed Scoped inner. Covers dict-cache hit / miss, invalidation, empty view
/// behavior, and that cache-hit reads never resolve the inner. Issue #720.
/// </summary>
public class CachingShiftViewServiceTests
{
    private readonly IShiftView _inner = Substitute.For<IShiftView>();

    private CachingShiftViewService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IShiftView>(
            CachingShiftViewService.InnerServiceKey, (_, _) => _inner);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new CachingShiftViewService(
            scopeFactory,
            NullLogger<CachingShiftViewService>.Instance);
    }

    // ── GetUserAsync ────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetUserAsync_DictMiss_DelegatesToInner_AndCachesResult()
    {
        var userId = Guid.NewGuid();
        var view = new ShiftUserView(
            userId, Profile: null, Availability: null, BuildStatus: null,
            TagPreferences: [],
            Signups: []);
        _inner.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(view));

        var sut = CreateSut();

        var first = await sut.GetUserAsync(userId);
        first.Should().BeSameAs(view);
        await _inner.Received(1).GetUserAsync(userId, Arg.Any<CancellationToken>());

        var second = await sut.GetUserAsync(userId);
        second.Should().BeSameAs(view);
        // Hot path: no further calls to inner.
        await _inner.Received(1).GetUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserAsync_AfterInvalidate_ReloadsFromInner()
    {
        var userId = Guid.NewGuid();
        var view1 = new ShiftUserView(
            userId, null, null, null,
            [], []);
        var view2 = view1 with { Signups = [new ShiftSignup { Id = Guid.NewGuid(), UserId = userId }] };

        _inner.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(view1), new ValueTask<ShiftUserView>(view2));

        var sut = CreateSut();

        (await sut.GetUserAsync(userId)).Should().BeSameAs(view1);
        sut.InvalidateUser(userId);
        (await sut.GetUserAsync(userId)).Should().BeSameAs(view2);

        await _inner.Received(2).GetUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUsersAsync_BatchPopulatesDict_AndSubsequentSingleReadsHitCache()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var v1 = new ShiftUserView(u1, null, null, null,
            [], []);
        var v2 = new ShiftUserView(u2, null, null, null,
            [], []);
        _inner.GetUserAsync(u1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(v1));
        _inner.GetUserAsync(u2, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(v2));

        var sut = CreateSut();

        var batch = await sut.GetUsersAsync([u1, u2]);
        batch.Should().ContainKeys(u1, u2);
        batch[u1].Should().BeSameAs(v1);
        batch[u2].Should().BeSameAs(v2);

        (await sut.GetUserAsync(u1)).Should().BeSameAs(v1);
        (await sut.GetUserAsync(u2)).Should().BeSameAs(v2);

        await _inner.Received(1).GetUserAsync(u1, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserAsync(u2, Arg.Any<CancellationToken>());
    }

    // ── GetRotaAsync ────────────────────────────────────────────────────────

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

        (await sut.GetRotaAsync(rotaId)).Should().BeSameAs(view);
        (await sut.GetRotaAsync(rotaId)).Should().BeSameAs(view);

        await _inner.Received(1).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAll_ClearsBothDicts()
    {
        var userId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        _inner.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(new ShiftUserView(
                userId, null, null, null,
                [], [])));
        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(new ShiftRotaView(
                rotaId, null, [], [], [])));

        var sut = CreateSut();
        await sut.GetUserAsync(userId);
        await sut.GetRotaAsync(rotaId);

        sut.InvalidateAll();

        await sut.GetUserAsync(userId);
        await sut.GetRotaAsync(rotaId);

        await _inner.Received(2).GetUserAsync(userId, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
    }

    // ── InvalidateRota cascade to affected users ─────────────────────────────

    [HumansFact]
    public async Task InvalidateRota_EvictsCachedUserViewsReferencingTheRota()
    {
        var rotaId = Guid.NewGuid();
        var userOnRota = Guid.NewGuid();
        var unrelatedUser = Guid.NewGuid();

        var shiftOnRota = new Shift { Id = Guid.NewGuid(), RotaId = rotaId };
        var unrelatedShift = new Shift { Id = Guid.NewGuid(), RotaId = Guid.NewGuid() };

        var rotaView = new ShiftRotaView(
            rotaId, Rota: null,
            Shifts: [shiftOnRota],
            Tags: [],
            Signups: []);

        var userOnRotaView = new ShiftUserView(
            userOnRota, null, null, null,
            [], [new ShiftSignup { Id = Guid.NewGuid(), UserId = userOnRota, ShiftId = shiftOnRota.Id, Shift = shiftOnRota }
            ]);

        var unrelatedUserView = new ShiftUserView(
            unrelatedUser, null, null, null,
            [], [new ShiftSignup { Id = Guid.NewGuid(), UserId = unrelatedUser, ShiftId = unrelatedShift.Id, Shift = unrelatedShift }
            ]);

        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(rotaView));
        _inner.GetUserAsync(userOnRota, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(userOnRotaView));
        _inner.GetUserAsync(unrelatedUser, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(unrelatedUserView));

        var sut = CreateSut();
        await sut.GetRotaAsync(rotaId);
        await sut.GetUserAsync(userOnRota);
        await sut.GetUserAsync(unrelatedUser);

        sut.InvalidateRota(rotaId);

        // Rota cache and the user with a signup on it reload; unrelated user
        // stays cached.
        await sut.GetRotaAsync(rotaId);
        await sut.GetUserAsync(userOnRota);
        await sut.GetUserAsync(unrelatedUser);

        await _inner.Received(2).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetUserAsync(userOnRota, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserAsync(unrelatedUser, Arg.Any<CancellationToken>());
    }

    // ── InvalidateShift fan-out from the live snapshot ───────────────────────

    [HumansFact]
    public async Task InvalidateShift_EvictsAffectedRotaAndUsers()
    {
        var shiftId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var unrelatedUserId = Guid.NewGuid();

        var shift = new Shift { Id = shiftId, RotaId = rotaId };
        var rotaView = new ShiftRotaView(
            rotaId, Rota: null,
            Shifts: [shift],
            Tags: [],
            Signups: []);

        var userView = new ShiftUserView(
            userId, null, null, null,
            [], [new ShiftSignup { Id = Guid.NewGuid(), UserId = userId, ShiftId = shiftId }]);

        var unrelatedUserView = new ShiftUserView(
            unrelatedUserId, null, null, null,
            [], [new ShiftSignup { Id = Guid.NewGuid(), UserId = unrelatedUserId, ShiftId = Guid.NewGuid() }]);

        _inner.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftRotaView>(rotaView));
        _inner.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(userView));
        _inner.GetUserAsync(unrelatedUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(unrelatedUserView));

        var sut = CreateSut();

        // Prime the cache so InvalidateShift has something to walk.
        await sut.GetRotaAsync(rotaId);
        await sut.GetUserAsync(userId);
        await sut.GetUserAsync(unrelatedUserId);

        sut.InvalidateShift(shiftId);

        // Affected entries reload.
        await sut.GetRotaAsync(rotaId);
        await sut.GetUserAsync(userId);
        // Unrelated user stays cached.
        await sut.GetUserAsync(unrelatedUserId);

        await _inner.Received(2).GetRotaAsync(rotaId, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetUserAsync(userId, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetUserAsync(unrelatedUserId, Arg.Any<CancellationToken>());
    }
}
