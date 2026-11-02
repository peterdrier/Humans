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
