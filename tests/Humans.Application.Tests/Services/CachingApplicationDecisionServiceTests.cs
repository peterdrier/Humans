using AwesomeAssertions;
using NodaTime;
using NSubstitute;
using Humans.Application.DTOs.Governance;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Governance;
using Humans.Infrastructure.Stores;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CachingApplicationDecisionService"/>. These cover
/// the decorator's three responsibilities: read short-circuits via the store,
/// pass-through of writes to the inner service, and cross-cutting cache
/// invalidation after successful writes.
/// </summary>
public sealed class CachingApplicationDecisionServiceTests
{
    private readonly IApplicationDecisionService _inner = Substitute.For<IApplicationDecisionService>();
    private readonly ApplicationStore _store = new();
    private readonly IApplicationRepository _repository = Substitute.For<IApplicationRepository>();
    private readonly INavBadgeCacheInvalidator _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
    private readonly INotificationMeterCacheInvalidator _notificationMeter = Substitute.For<INotificationMeterCacheInvalidator>();
    private readonly IVotingBadgeCacheInvalidator _votingBadge = Substitute.For<IVotingBadgeCacheInvalidator>();
    private readonly CachingApplicationDecisionService _decorator;

    public CachingApplicationDecisionServiceTests()
    {
        _decorator = new CachingApplicationDecisionService(
            _inner,
            _store,
            _repository,
            _navBadge,
            _notificationMeter,
            _votingBadge);
    }

    [Fact]
    public async Task GetUserApplicationsAsync_ServedFromStoreWithoutCallingInner()
    {
        var userId = Guid.NewGuid();
        var app = NewApp(userId: userId);
        _store.Upsert(app);

        var result = await _decorator.GetUserApplicationsAsync(userId);

        result.Should().ContainSingle(a => a.Id == app.Id);
        await _inner.DidNotReceive().GetUserApplicationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_Success_InvalidatesNavBadgeAndNotificationMeter()
    {
        var appId = Guid.NewGuid();
        _repository.GetVoterIdsForApplicationAsync(appId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());
        _inner.ApproveAsync(appId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<LocalDate?>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(true));

        await _decorator.ApproveAsync(appId, Guid.NewGuid(), "ok", null);

        _navBadge.Received().Invalidate();
        _notificationMeter.Received().Invalidate();
    }

    [Fact]
    public async Task ApproveAsync_Success_InvalidatesEveryVoterBadge()
    {
        var appId = Guid.NewGuid();
        var voter1 = Guid.NewGuid();
        var voter2 = Guid.NewGuid();
        _repository.GetVoterIdsForApplicationAsync(appId, Arg.Any<CancellationToken>())
            .Returns(new[] { voter1, voter2 });
        _inner.ApproveAsync(appId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<LocalDate?>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(true));

        await _decorator.ApproveAsync(appId, Guid.NewGuid(), "ok", null);

        _votingBadge.Received().Invalidate(voter1);
        _votingBadge.Received().Invalidate(voter2);
    }

    [Fact]
    public async Task ApproveAsync_FetchesVoterIdsBeforeCallingInner()
    {
        var appId = Guid.NewGuid();
        var voter = Guid.NewGuid();
        var callOrder = new List<string>();
        _repository.GetVoterIdsForApplicationAsync(appId, Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("repo.GetVoterIds"); return Task.FromResult<IReadOnlyList<Guid>>(new[] { voter }); });
        _inner.ApproveAsync(appId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<LocalDate?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("inner.Approve"); return Task.FromResult(new ApplicationDecisionResult(true)); });

        await _decorator.ApproveAsync(appId, Guid.NewGuid(), "ok", null);

        callOrder.Should().Equal("repo.GetVoterIds", "inner.Approve");
    }

    [Fact]
    public async Task ApproveAsync_Failure_DoesNotInvalidateCaches()
    {
        var appId = Guid.NewGuid();
        _repository.GetVoterIdsForApplicationAsync(appId, Arg.Any<CancellationToken>())
            .Returns(new[] { Guid.NewGuid() });
        _inner.ApproveAsync(appId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<LocalDate?>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(false, "NotFound"));

        await _decorator.ApproveAsync(appId, Guid.NewGuid(), "ok", null);

        _navBadge.DidNotReceive().Invalidate();
        _notificationMeter.DidNotReceive().Invalidate();
        _votingBadge.DidNotReceive().Invalidate(Arg.Any<Guid>());
    }

    [Fact]
    public async Task RejectAsync_Success_InvalidatesAllThreeCaches()
    {
        var appId = Guid.NewGuid();
        var voter = Guid.NewGuid();
        _repository.GetVoterIdsForApplicationAsync(appId, Arg.Any<CancellationToken>())
            .Returns(new[] { voter });
        _inner.RejectAsync(appId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<LocalDate?>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(true));

        await _decorator.RejectAsync(appId, Guid.NewGuid(), "reason", null);

        _navBadge.Received().Invalidate();
        _notificationMeter.Received().Invalidate();
        _votingBadge.Received().Invalidate(voter);
    }

    [Fact]
    public async Task SubmitAsync_Success_InvalidatesNavBadgeAndNotificationMeter()
    {
        _inner.SubmitAsync(
                Arg.Any<Guid>(), Arg.Any<MembershipTier>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(true, ApplicationId: Guid.NewGuid()));

        await _decorator.SubmitAsync(
            Guid.NewGuid(), MembershipTier.Colaborador, "m",
            null, null, null, "en");

        _navBadge.Received().Invalidate();
        _notificationMeter.Received().Invalidate();
        _votingBadge.DidNotReceive().Invalidate(Arg.Any<Guid>());
    }

    [Fact]
    public async Task SubmitAsync_Failure_DoesNotInvalidate()
    {
        _inner.SubmitAsync(
                Arg.Any<Guid>(), Arg.Any<MembershipTier>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(false, "AlreadyPending"));

        await _decorator.SubmitAsync(
            Guid.NewGuid(), MembershipTier.Colaborador, "m",
            null, null, null, "en");

        _navBadge.DidNotReceive().Invalidate();
        _notificationMeter.DidNotReceive().Invalidate();
    }

    [Fact]
    public async Task WithdrawAsync_Success_InvalidatesNavBadgeAndNotificationMeter()
    {
        _inner.WithdrawAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationDecisionResult(true));

        await _decorator.WithdrawAsync(Guid.NewGuid(), Guid.NewGuid());

        _navBadge.Received().Invalidate();
        _notificationMeter.Received().Invalidate();
    }

    [Fact]
    public async Task GetApplicationDetailAsync_PassesThroughToInner()
    {
        var appId = Guid.NewGuid();
        var dto = new ApplicationAdminDetailDto(
            Id: appId,
            UserId: Guid.NewGuid(),
            UserEmail: "a@t.com",
            UserDisplayName: "A",
            UserProfilePictureUrl: null,
            Status: ApplicationStatus.Submitted,
            MembershipTier: MembershipTier.Colaborador,
            Motivation: "m",
            AdditionalInfo: null,
            SignificantContribution: null,
            RoleUnderstanding: null,
            Language: "en",
            SubmittedAt: Instant.FromUtc(2026, 3, 1, 12, 0),
            ReviewStartedAt: null,
            ResolvedAt: null,
            ReviewerName: null,
            ReviewNotes: null,
            History: Array.Empty<ApplicationStateHistoryDto>());
        _inner.GetApplicationDetailAsync(appId, Arg.Any<CancellationToken>()).Returns(dto);

        var result = await _decorator.GetApplicationDetailAsync(appId);

        result.Should().BeSameAs(dto);
    }

    private static MemberApplication NewApp(Guid? id = null, Guid? userId = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        UserId = userId ?? Guid.NewGuid(),
        MembershipTier = MembershipTier.Colaborador,
        Motivation = "m",
        SubmittedAt = Instant.FromUtc(2026, 3, 1, 12, 0),
        UpdatedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
    };
}
