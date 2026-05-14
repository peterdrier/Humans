using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Shifts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

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

    // ── GetUser ─────────────────────────────────────────────────────────────

    [HumansFact]
    public void GetUser_DictMiss_DelegatesToInner_AndCachesResult()
    {
        var userId = Guid.NewGuid();
        var view = new ShiftUserView(
            userId, Profile: null, Availability: null, BuildStatus: null,
            TagPreferences: Array.Empty<VolunteerTagPreference>(),
            Signups: Array.Empty<ShiftSignup>());
        _inner.GetUser(userId).Returns(view);

        var sut = CreateSut();

        var first = sut.GetUser(userId);
        first.Should().BeSameAs(view);
        _inner.Received(1).GetUser(userId);

        var second = sut.GetUser(userId);
        second.Should().BeSameAs(view);
        // Hot path: no further calls to inner.
        _inner.Received(1).GetUser(userId);
    }

    [HumansFact]
    public void GetUser_AfterInvalidate_ReloadsFromInner()
    {
        var userId = Guid.NewGuid();
        var view1 = new ShiftUserView(
            userId, null, null, null,
            Array.Empty<VolunteerTagPreference>(), Array.Empty<ShiftSignup>());
        var view2 = view1 with { Signups = new[] { new ShiftSignup { Id = Guid.NewGuid(), UserId = userId } } };

        _inner.GetUser(userId).Returns(view1, view2);

        var sut = CreateSut();

        sut.GetUser(userId).Should().BeSameAs(view1);
        sut.InvalidateUser(userId);
        sut.GetUser(userId).Should().BeSameAs(view2);

        _inner.Received(2).GetUser(userId);
    }

    [HumansFact]
    public void GetUsers_BatchPopulatesDict_AndSubsequentSingleReadsHitCache()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var v1 = new ShiftUserView(u1, null, null, null,
            Array.Empty<VolunteerTagPreference>(), Array.Empty<ShiftSignup>());
        var v2 = new ShiftUserView(u2, null, null, null,
            Array.Empty<VolunteerTagPreference>(), Array.Empty<ShiftSignup>());
        _inner.GetUser(u1).Returns(v1);
        _inner.GetUser(u2).Returns(v2);

        var sut = CreateSut();

        var batch = sut.GetUsers(new[] { u1, u2 });
        batch.Should().ContainKeys(u1, u2);
        batch[u1].Should().BeSameAs(v1);
        batch[u2].Should().BeSameAs(v2);

        sut.GetUser(u1).Should().BeSameAs(v1);
        sut.GetUser(u2).Should().BeSameAs(v2);

        _inner.Received(1).GetUser(u1);
        _inner.Received(1).GetUser(u2);
    }

    // ── GetRota ─────────────────────────────────────────────────────────────

    [HumansFact]
    public void GetRota_DictMiss_DelegatesToInner_AndCachesResult()
    {
        var rotaId = Guid.NewGuid();
        var view = new ShiftRotaView(
            rotaId, Rota: null,
            Shifts: Array.Empty<Shift>(),
            Tags: Array.Empty<ShiftTag>(),
            Signups: Array.Empty<ShiftSignup>());
        _inner.GetRota(rotaId).Returns(view);

        var sut = CreateSut();

        sut.GetRota(rotaId).Should().BeSameAs(view);
        sut.GetRota(rotaId).Should().BeSameAs(view);

        _inner.Received(1).GetRota(rotaId);
    }

    [HumansFact]
    public void InvalidateAll_ClearsBothDicts()
    {
        var userId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        _inner.GetUser(userId).Returns(new ShiftUserView(
            userId, null, null, null,
            Array.Empty<VolunteerTagPreference>(), Array.Empty<ShiftSignup>()));
        _inner.GetRota(rotaId).Returns(new ShiftRotaView(
            rotaId, null, Array.Empty<Shift>(), Array.Empty<ShiftTag>(), Array.Empty<ShiftSignup>()));

        var sut = CreateSut();
        sut.GetUser(userId);
        sut.GetRota(rotaId);

        sut.InvalidateAll();

        sut.GetUser(userId);
        sut.GetRota(rotaId);

        _inner.Received(2).GetUser(userId);
        _inner.Received(2).GetRota(rotaId);
    }

    // ── InvalidateShift fan-out from the live snapshot ───────────────────────

    [HumansFact]
    public void InvalidateShift_EvictsAffectedRotaAndUsers()
    {
        var shiftId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var unrelatedUserId = Guid.NewGuid();

        var shift = new Shift { Id = shiftId, RotaId = rotaId };
        var rotaView = new ShiftRotaView(
            rotaId, Rota: null,
            Shifts: new[] { shift },
            Tags: Array.Empty<ShiftTag>(),
            Signups: Array.Empty<ShiftSignup>());

        var userView = new ShiftUserView(
            userId, null, null, null,
            Array.Empty<VolunteerTagPreference>(),
            new[] { new ShiftSignup { Id = Guid.NewGuid(), UserId = userId, ShiftId = shiftId } });

        var unrelatedUserView = new ShiftUserView(
            unrelatedUserId, null, null, null,
            Array.Empty<VolunteerTagPreference>(),
            new[] { new ShiftSignup { Id = Guid.NewGuid(), UserId = unrelatedUserId, ShiftId = Guid.NewGuid() } });

        _inner.GetRota(rotaId).Returns(rotaView);
        _inner.GetUser(userId).Returns(userView);
        _inner.GetUser(unrelatedUserId).Returns(unrelatedUserView);

        var sut = CreateSut();

        // Prime the cache so InvalidateShift has something to walk.
        sut.GetRota(rotaId);
        sut.GetUser(userId);
        sut.GetUser(unrelatedUserId);

        sut.InvalidateShift(shiftId);

        // Affected entries reload.
        sut.GetRota(rotaId);
        sut.GetUser(userId);
        // Unrelated user stays cached.
        sut.GetUser(unrelatedUserId);

        _inner.Received(2).GetRota(rotaId);
        _inner.Received(2).GetUser(userId);
        _inner.Received(1).GetUser(unrelatedUserId);
    }
}
