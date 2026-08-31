using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Consent.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Consent.Tests.Services;

/// <summary>
/// Issue #747. Pins the bulk-bucket behavior of
/// <see cref="CachingConsentService.GetConsentMapForUsersAsync"/>: a
/// cold-cache call must issue a single bulk inner call (not N per-id
/// loads), and the result must be cached for the next call.
/// </summary>
public sealed class CachingConsentServiceTests
{
    private readonly IConsentService _inner = Substitute.For<IConsentService>();
    private readonly ILegalDocumentSyncService _legalSync = Substitute.For<ILegalDocumentSyncService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();

    private CachingConsentService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IConsentService>(
            CachingConsentService.InnerServiceKey, (_, _) => _inner);
        services.AddScoped<IUserServiceRead>(_ => _userService);
        var scopeFactory = services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new CachingConsentService(
            _legalSync, _clock,
            scopeFactory,
            NullLogger<CachingConsentService>.Instance);
    }

    [HumansFact]
    public async Task GetConsentMapForUsersAsync_ColdCache_50Ids_IssuesSingleBulkInnerCall()
    {
        var userIds = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        var stub = userIds.ToDictionary(
            id => id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid> { Guid.NewGuid() });
        _inner.GetConsentMapForUsersAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(stub);

        var sut = CreateSut();

        var result = await sut.GetConsentMapForUsersAsync(userIds, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(50);
        foreach (var id in userIds)
            result[id].Should().BeEquivalentTo(stub[id]);

        // The acceptance criterion: a single bulk inner call, not a
        // per-id loop.
        await _inner.Received(1).GetConsentMapForUsersAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 50),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetConsentMapForUsersAsync_AllHits_NoInnerCall()
    {
        var userIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var firstCallStub = userIds.ToDictionary(
            id => id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid> { Guid.NewGuid() });
        _inner.GetConsentMapForUsersAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(firstCallStub);

        var sut = CreateSut();

        // Warm the cache.
        _ = await sut.GetConsentMapForUsersAsync(userIds, Xunit.TestContext.Current.CancellationToken);
        _inner.ClearReceivedCalls();

        // All-hit path: no scope, no inner call.
        var second = await sut.GetConsentMapForUsersAsync(userIds, Xunit.TestContext.Current.CancellationToken);

        second.Should().HaveCount(10);
        foreach (var id in userIds)
            second[id].Should().BeEquivalentTo(firstCallStub[id]);

        await _inner.DidNotReceive().GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetConsentMapForUsersAsync_DuplicateColdIds_DedupedBeforeInnerCall()
    {
        // Caller passes the same uncached id multiple times. The inner
        // bulk call must see each id once, not redo merge/source work
        // per duplicate.
        var id = Guid.NewGuid();
        var userIds = new List<Guid> { id, id, id };
        var stub = new Dictionary<Guid, IReadOnlySet<Guid>>
        {
            [id] = new HashSet<Guid> { Guid.NewGuid() },
        };
        _inner.GetConsentMapForUsersAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(stub);

        var sut = CreateSut();
        var result = await sut.GetConsentMapForUsersAsync(userIds, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[id].Should().BeEquivalentTo(stub[id]);

        await _inner.Received(1).GetConsentMapForUsersAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == id),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetConsentMapForUsersAsync_PartialHits_OnlyMissesGoToInner()
    {
        var hitIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var missIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        var hitStub = hitIds.ToDictionary(
            id => id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid> { Guid.NewGuid() });
        _inner.GetConsentMapForUsersAsync(
                Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == hitIds.Count),
                Arg.Any<CancellationToken>())
            .Returns(hitStub);

        var sut = CreateSut();
        _ = await sut.GetConsentMapForUsersAsync(hitIds, Xunit.TestContext.Current.CancellationToken); // warm

        var missStub = missIds.ToDictionary(
            id => id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid> { Guid.NewGuid() });
        _inner.GetConsentMapForUsersAsync(
                Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == missIds.Count
                    && ids.All(missIds.Contains)),
                Arg.Any<CancellationToken>())
            .Returns(missStub);

        var combined = hitIds.Concat(missIds).ToList();
        var result = await sut.GetConsentMapForUsersAsync(combined, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(7);

        // Inner saw exactly the miss set in the second call — not all 7 ids.
        await _inner.Received(1).GetConsentMapForUsersAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == missIds.Count
                && ids.All(missIds.Contains)),
            Arg.Any<CancellationToken>());
    }

    // ── SubmitConsentAsync — synchronous cache refresh ───────────────────────

    [HumansFact]
    public async Task SubmitConsentAsync_Success_RefreshesRowBeforeReturning_SoNextReadHitsCache()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        _userService.GetMergedSourceIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());
        _inner.SubmitConsentAsync(userId, versionId, true, "1.2.3.4", "agent", Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(true, "Privacy"));
        _inner.GetConsentedVersionIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid> { versionId });

        var sut = CreateSut();
        var result = await sut.SubmitConsentAsync(
            userId, versionId, true, "1.2.3.4", "agent", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        // The refresh ran inline, through the same loader the lazy path uses.
        await _inner.Received(1).GetConsentedVersionIdsAsync(userId, Arg.Any<CancellationToken>());

        // And the refreshed row serves the next read from cache — no inner call.
        var map = await sut.GetConsentMapForUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);
        map[userId].Should().Contain(versionId);
        await _inner.DidNotReceive().GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitConsentAsync_Failure_DoesNotRefreshCache()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        _userService.GetMergedSourceIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());
        _inner.SubmitConsentAsync(userId, versionId, true, "1.2.3.4", "agent", Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(false, ErrorKey: "Consent_AlreadyConsented"));

        var sut = CreateSut();
        var result = await sut.SubmitConsentAsync(
            userId, versionId, true, "1.2.3.4", "agent", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        // A failed submit changed nothing, so the cache row must not be touched.
        await _inner.DidNotReceive().GetConsentedVersionIdsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
