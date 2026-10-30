using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Jobs;
using Humans.Governance.Services;
using Humans.Governance.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Governance.Tests.Services;

/// <summary>
/// The daily tier term expiry: both tiers expire against today's UTC date, and every downgrade
/// Governance returns (including the fall-back to a still-in-term other tier) gets its
/// <see cref="GovernanceAuditActions.TierDowngraded"/> entry. The downgrade rule itself is covered by
/// <c>MembershipTierServiceTests</c>.
/// </summary>
public sealed class MembershipTierExpiryJobTests
{
    private static readonly LocalDate Today = new(2028, 1, 1);

    private readonly IGovernanceManagement _governance = Substitute.For<IGovernanceManagement>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly MembershipTierExpiryJob _job;

    public MembershipTierExpiryJobTests()
    {
        _governance.ExpireMembershipTiersAsync(Arg.Any<MembershipTier>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>([]));
        _job = new MembershipTierExpiryJob(_governance, _users, _audit, Substitute.For<IHumansMetrics>(),
            NullLogger<MembershipTierExpiryJob>.Instance, new FakeClock(Instant.FromUtc(2028, 1, 1, 0, 5)));
    }

    private void Downgrades(MembershipTier tier, params (Guid UserId, MembershipTier NewTier)[] rows) =>
        _governance.ExpireMembershipTiersAsync(tier, Today, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>(rows));

    [HumansFact]
    public async Task ExecuteAsync_ExpiresBothTiers_AndAuditsEachDowngrade()
    {
        var lapsed = Guid.NewGuid();
        var fallsBack = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        Downgrades(MembershipTier.Asociado, (lapsed, MembershipTier.Volunteer), (fallsBack, MembershipTier.Colaborador));
        Downgrades(MembershipTier.Colaborador, (unknown, MembershipTier.Volunteer));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>
            {
                [lapsed] = new User { Id = lapsed, BurnerName = "Ana" }.ToUserInfo(),
                [fallsBack] = new User { Id = fallsBack, BurnerName = "Bea" }.ToUserInfo(),
            }));

        await _job.ExecuteAsync(CancellationToken.None);

        await _governance.Received(1).ExpireMembershipTiersAsync(MembershipTier.Asociado, Today, Arg.Any<CancellationToken>());
        await _governance.Received(1).ExpireMembershipTiersAsync(MembershipTier.Colaborador, Today, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(GovernanceAuditActions.TierDowngraded, "Profile", lapsed,
            "Membership tier changed to Volunteer for Ana due to Asociado term expiry",
            nameof(MembershipTierExpiryJob), lapsed, nameof(User));
        await _audit.Received(1).LogAsync(GovernanceAuditActions.TierDowngraded, "Profile", fallsBack,
            "Membership tier changed to Colaborador for Bea due to Asociado term expiry",
            nameof(MembershipTierExpiryJob), fallsBack, nameof(User));
        await _audit.Received(1).LogAsync(GovernanceAuditActions.TierDowngraded, "Profile", unknown,
            "Membership tier changed to Volunteer for Unknown due to Colaborador term expiry",
            nameof(MembershipTierExpiryJob), unknown, nameof(User));
    }

    [HumansFact]
    public async Task ExecuteAsync_NothingLapsed_WritesNoAudit()
    {
        await _job.ExecuteAsync(CancellationToken.None);

        _audit.ReceivedCalls().Should().BeEmpty();
        await _users.DidNotReceiveWithAnyArgs().GetUserInfosAsync(default!, default);
    }
}
