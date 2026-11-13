using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Jobs;
using Humans.Governance.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Governance.Tests.Services;

/// <summary>The daily tier expiry runs both tiers against today's UTC date.</summary>
public sealed class MembershipTierExpiryJobTests
{
    private static readonly LocalDate Today = new(2028, 1, 1);

    private readonly IGovernanceManagement _governance = Substitute.For<IGovernanceManagement>();
    private readonly MembershipTierExpiryJob _job;

    public MembershipTierExpiryJobTests()
    {
        _governance.ExpireMembershipTiersAsync(Arg.Any<MembershipTier>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>([]));
        _job = new MembershipTierExpiryJob(_governance, Substitute.For<IHumansMetrics>(),
            NullLogger<MembershipTierExpiryJob>.Instance, new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));
    }

    private void Downgrades(MembershipTier tier, params (Guid UserId, MembershipTier NewTier)[] rows) =>
        _governance.ExpireMembershipTiersAsync(tier, Today, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>(rows));

    [HumansTheory]
    [Xunit.InlineData("Shutdown")]
    [Xunit.InlineData("DependencyCancellation")]
    [Xunit.InlineData("Fault")]
    public async Task ExpiryJob_DistinguishesShutdownFromFaults(string kind)
    {
        using var shutdown = new CancellationTokenSource();
        var failure = string.Equals(kind, "Fault", StringComparison.Ordinal)
            ? (Exception)new IOException("Tier expiry unavailable")
            : new OperationCanceledException(string.Equals(kind, "Shutdown", StringComparison.Ordinal)
                ? shutdown.Token : new CancellationToken(true));
        _governance.ExpireMembershipTiersAsync(MembershipTier.Asociado, Today, shutdown.Token)
            .Returns(async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> (_) =>
            {
                if (string.Equals(kind, "Shutdown", StringComparison.Ordinal)) await shutdown.CancelAsync();
                throw failure;
            });
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<MembershipTierExpiryJob>();
        var job = new MembershipTierExpiryJob(_governance, metrics, logger,
            new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        metrics.DidNotReceive().RecordJobRun("membership_tier_expiry", "success");
        if (string.Equals(kind, "Shutdown", StringComparison.Ordinal))
        {
            metrics.DidNotReceive().RecordJobRun("membership_tier_expiry", "failure");
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
            logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        }
        else metrics.Received(1).RecordJobRun("membership_tier_expiry", "failure");
    }

    [HumansFact]
    public async Task ExpiryJob_StopsBeforeStartingTheNextTier()
    {
        using var shutdown = new CancellationTokenSource();
        _governance.ExpireMembershipTiersAsync(MembershipTier.Asociado, Today, shutdown.Token)
            .Returns(async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> (_) =>
            {
                await shutdown.CancelAsync();
                return [];
            });
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<MembershipTierExpiryJob>();
        var job = new MembershipTierExpiryJob(_governance, metrics, logger,
            new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeAssignableTo<OperationCanceledException>();
        await _governance.DidNotReceive().ExpireMembershipTiersAsync(
            MembershipTier.Colaborador, Today, Arg.Any<CancellationToken>());
        metrics.DidNotReceive().RecordJobRun("membership_tier_expiry", "success");
        metrics.DidNotReceive().RecordJobRun("membership_tier_expiry", "failure");
    }

    [HumansFact]
    public async Task ExecuteAsync_ExpiresBothTiers()
    {
        var lapsed = Guid.NewGuid();
        var fallsBack = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        Downgrades(MembershipTier.Asociado, (lapsed, MembershipTier.Volunteer), (fallsBack, MembershipTier.Colaborador));
        Downgrades(MembershipTier.Colaborador, (unknown, MembershipTier.Volunteer));
        await _job.ExecuteAsync(CancellationToken.None);

        await _governance.Received(1).ExpireMembershipTiersAsync(MembershipTier.Asociado, Today, Arg.Any<CancellationToken>());
        await _governance.Received(1).ExpireMembershipTiersAsync(MembershipTier.Colaborador, Today, Arg.Any<CancellationToken>());

    }
}
