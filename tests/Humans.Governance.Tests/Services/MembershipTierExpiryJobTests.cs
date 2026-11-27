using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Users.Contracts;
using MemberApplication = Humans.Governance.Domain.Application;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly IApplicationRepository _applications = Substitute.For<IApplicationRepository>();
    private readonly IMembershipTierRepository _tiers = Substitute.For<IMembershipTierRepository>();
    private readonly MembershipTierService _governance;
    private readonly MembershipTierExpiryJob _job;

    public MembershipTierExpiryJobTests()
    {
        _applications.GetActiveApprovedTierUserIdsAsync(Arg.Any<MembershipTier>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));
        _applications.GetOtherActiveTierAssignmentsAsync(Arg.Any<MembershipTier>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, MembershipTier>>(new Dictionary<Guid, MembershipTier>()));
        _tiers.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<MembershipTierRecord>>([]));
        var users = Substitute.For<IUserService>();
        _governance = new MembershipTierService(_tiers, _applications, users,
            Substitute.For<IAuditLogService>(),
            new CachingGovernanceService(Substitute.For<IServiceScopeFactory>(), users,
                NullLogger<CachingGovernanceService>.Instance),
            new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));
        _job = new MembershipTierExpiryJob(_governance, Substitute.For<IHumansMetrics>(),
            NullLogger<MembershipTierExpiryJob>.Instance, new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));
    }

    [HumansTheory]
    [Xunit.InlineData(ExpiryFailureKind.Shutdown)]
    [Xunit.InlineData(ExpiryFailureKind.DependencyCancellation)]
    [Xunit.InlineData(ExpiryFailureKind.Fault)]
    public async Task ExpiryJob_DistinguishesShutdownFromFaults(ExpiryFailureKind kind)
    {
        using var shutdown = new CancellationTokenSource();
        var failure = kind == ExpiryFailureKind.Fault
            ? (Exception)new IOException("Tier expiry unavailable")
            : new OperationCanceledException(kind == ExpiryFailureKind.Shutdown
                ? shutdown.Token : new CancellationToken(true));
        _applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Today, shutdown.Token)
            .Returns(async Task<IReadOnlyList<Guid>> (_) =>
            {
                if (kind == ExpiryFailureKind.Shutdown) await shutdown.CancelAsync();
                throw failure;
            });
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<MembershipTierExpiryJob>();
        var job = new MembershipTierExpiryJob(_governance, metrics, logger,
            new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        metrics.DidNotReceive().RecordJobRun("membership_tier_expiry", "success");
        if (kind == ExpiryFailureKind.Shutdown)
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
        _applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Today, shutdown.Token)
            .Returns(async Task<IReadOnlyList<Guid>> (_) =>
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
        await _applications.DidNotReceive().GetActiveApprovedTierUserIdsAsync(
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
        var rows = new Dictionary<Guid, MembershipTierRecord>
        {
            [lapsed] = new() { UserId = lapsed, Tier = MembershipTier.Asociado },
            [fallsBack] = new() { UserId = fallsBack, Tier = MembershipTier.Asociado },
            [unknown] = new() { UserId = unknown, Tier = MembershipTier.Colaborador },
        };
        _tiers.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<MembershipTierRecord>>(rows.Values.ToList()));
        _tiers.UpsertAsync(Arg.Any<MembershipTierRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var row = call.Arg<MembershipTierRecord>();
            rows[row.UserId] = row;
            return Task.CompletedTask;
        });
        _applications.GetOtherActiveTierAssignmentsAsync(MembershipTier.Asociado, Today, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, MembershipTier>>(
                new Dictionary<Guid, MembershipTier> { [fallsBack] = MembershipTier.Colaborador }));
        _applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Colaborador, Today, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([fallsBack]));
        var grant = new MemberApplication { Id = Guid.NewGuid(), UserId = fallsBack, MembershipTier = MembershipTier.Colaborador };
        grant.Approve(Guid.NewGuid(), null, new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));
        grant.TermExpiresAt = Today.PlusDays(1);
        _applications.GetByUserIdAsync(fallsBack, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemberApplication>>([grant]));
        await _job.ExecuteAsync(CancellationToken.None);

        await _applications.Received(1).GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Today, Arg.Any<CancellationToken>());
        await _applications.Received(1).GetActiveApprovedTierUserIdsAsync(MembershipTier.Colaborador, Today, Arg.Any<CancellationToken>());
        rows[lapsed].Tier.Should().Be(MembershipTier.Volunteer);
        rows[fallsBack].Tier.Should().Be(MembershipTier.Colaborador);
        rows[unknown].Tier.Should().Be(MembershipTier.Volunteer);
        await _tiers.Received(3).UpsertAsync(Arg.Any<MembershipTierRecord>(), Arg.Any<CancellationToken>());

    }
}

public enum ExpiryFailureKind { Shutdown, DependencyCancellation, Fault }
