using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Shifts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    // ── Startup warmup ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task StartAsync_BulkLoadsRepos_PopulatesUserCacheForEveryKnownUser_NoInnerFallback()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid(); // user with no shift data anywhere — gets an empty view from warmup
        var eventId = Guid.NewGuid();
        var activeEvent = new EventSettings { Id = eventId, IsActive = true, Year = 2026, EventName = "Test" };
        var u1Profile = new VolunteerEventProfile { Id = Guid.NewGuid(), UserId = u1 };
        var u1Avail = new GeneralAvailability { Id = Guid.NewGuid(), UserId = u1, EventSettingsId = eventId };
        var u2Build = new VolunteerBuildStatus { Id = Guid.NewGuid(), UserId = u2, EventSettingsId = eventId };
        var u1Signup = new ShiftSignup { Id = Guid.NewGuid(), UserId = u1, ShiftId = Guid.NewGuid() };
        var u1Tag = new VolunteerTagPreference { Id = Guid.NewGuid(), UserId = u1, ShiftTagId = Guid.NewGuid() };

        var users = Substitute.For<IUserRepository>();
        users.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<User>)[
                new User { Id = u1 }, new User { Id = u2 }, new User { Id = u3 }]);

        var management = Substitute.For<IShiftManagementRepository>();
        management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(activeEvent);
        management.GetAllVolunteerEventProfilesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VolunteerEventProfile>)[u1Profile]);

        var signups = Substitute.For<IShiftSignupRepository>();
        signups.GetAllVolunteerTagPreferencesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VolunteerTagPreference>)[u1Tag]);
        signups.GetAllByEventAsync(eventId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ShiftSignup>)[u1Signup]);

        var availability = Substitute.For<IGeneralAvailabilityRepository>();
        availability.GetByEventAsync(eventId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GeneralAvailability>)[u1Avail]);

        var tracking = Substitute.For<IVolunteerTrackingRepository>();
        tracking.GetByEventAsync(eventId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VolunteerBuildStatus>)[u2Build]);

        var services = new ServiceCollection();
        services.AddKeyedScoped<IShiftView>(CachingShiftViewService.InnerServiceKey, (_, _) => _inner);
        services.AddSingleton(users);
        services.AddSingleton(management);
        services.AddSingleton(signups);
        services.AddSingleton(availability);
        services.AddSingleton(tracking);
        var provider = services.BuildServiceProvider();

        var sut = new CachingShiftViewService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingShiftViewService>.Instance);

        await ((IHostedService)sut).StartAsync(CancellationToken.None);

        // u1: profile + availability + signup + tag
        var v1 = await sut.GetUserAsync(u1);
        v1.Profile.Should().BeSameAs(u1Profile);
        v1.Availability.Should().BeSameAs(u1Avail);
        v1.BuildStatus.Should().BeNull();
        v1.Signups.Should().ContainSingle().Which.Should().BeSameAs(u1Signup);
        v1.TagPreferences.Should().ContainSingle().Which.Should().BeSameAs(u1Tag);

        // u2: only build status
        var v2 = await sut.GetUserAsync(u2);
        v2.Profile.Should().BeNull();
        v2.BuildStatus.Should().BeSameAs(u2Build);
        v2.Signups.Should().BeEmpty();
        v2.TagPreferences.Should().BeEmpty();

        // u3: empty view, still served from cache — never hits the inner
        var v3 = await sut.GetUserAsync(u3);
        v3.Profile.Should().BeNull();
        v3.Availability.Should().BeNull();
        v3.BuildStatus.Should().BeNull();
        v3.Signups.Should().BeEmpty();
        v3.TagPreferences.Should().BeEmpty();

        // The whole point: warmup eliminated the per-user fan-out.
        await _inner.DidNotReceive().GetUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        sut.UserCacheStats.IsWarmedUp.Should().BeTrue();
    }

    [HumansFact]
    public async Task StartAsync_NoActiveEvent_SkipsEventScopedReads_StillPopulatesCacheWithProfilesAndTags()
    {
        var u1 = Guid.NewGuid();
        var u1Profile = new VolunteerEventProfile { Id = Guid.NewGuid(), UserId = u1 };

        var users = Substitute.For<IUserRepository>();
        users.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<User>)[new User { Id = u1 }]);

        var management = Substitute.For<IShiftManagementRepository>();
        management.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettings?)null);
        management.GetAllVolunteerEventProfilesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VolunteerEventProfile>)[u1Profile]);

        var signups = Substitute.For<IShiftSignupRepository>();
        signups.GetAllVolunteerTagPreferencesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VolunteerTagPreference>)[]);

        var availability = Substitute.For<IGeneralAvailabilityRepository>();
        var tracking = Substitute.For<IVolunteerTrackingRepository>();

        var services = new ServiceCollection();
        services.AddKeyedScoped<IShiftView>(CachingShiftViewService.InnerServiceKey, (_, _) => _inner);
        services.AddSingleton(users);
        services.AddSingleton(management);
        services.AddSingleton(signups);
        services.AddSingleton(availability);
        services.AddSingleton(tracking);
        var provider = services.BuildServiceProvider();

        var sut = new CachingShiftViewService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingShiftViewService>.Instance);

        await ((IHostedService)sut).StartAsync(CancellationToken.None);

        var view = await sut.GetUserAsync(u1);
        view.Profile.Should().BeSameAs(u1Profile);
        view.Availability.Should().BeNull();

        await availability.DidNotReceive().GetByEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await tracking.DidNotReceive().GetByEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await signups.DidNotReceive().GetAllByEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task StartAsync_WarmupFailure_IsSwallowed_LazyPathStillWorks()
    {
        // Empty scope — repo resolution throws inside warmup, but StartAsync must complete
        // and the lazy per-key fallback must still serve reads (no-startup-guards rule).
        var sut = CreateSut();
        await ((IHostedService)sut).StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        var view = new ShiftUserView(userId, null, null, null, [], []);
        _inner.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(view));

        (await sut.GetUserAsync(userId)).Should().BeSameAs(view);
        sut.UserCacheStats.IsWarmedUp.Should().BeFalse();
    }
}
