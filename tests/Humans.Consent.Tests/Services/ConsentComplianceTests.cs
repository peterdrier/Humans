using Xunit;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Consent.Contracts;
using Humans.Consent.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Consent.Tests.Services;

public sealed class ConsentComplianceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 1, 0, 0);

    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly ILegalDocumentSyncService _legalDocuments = Substitute.For<ILegalDocumentSyncService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roleAssignments = Substitute.For<IRoleAssignmentService>();
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _signed = [];
    private readonly ConsentCompliance _sut;

    public ConsentComplianceTests()
    {
        _legalDocuments.GetRequiredDocumentVersionsForTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _consents.GetConsentMapForUsersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Guid>>().ToDictionary(id => id, SignedBy));
        _consents.GetConsentedVersionIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => SignedBy(call.Arg<Guid>()));
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());

        _sut = new ConsentCompliance(
            _consents, _legalDocuments, _teams, _roleAssignments, Substitute.For<IUserService>(), new FakeClock(Now));
    }

    private IReadOnlySet<Guid> SignedBy(Guid userId) => _signed.GetValueOrDefault(userId) ?? new HashSet<Guid>();

    private Guid Require(Guid teamId, Instant? effectiveFrom = null, int graceDays = 7)
    {
        var version = new RequiredDocumentVersionSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), "Doc", graceDays, "v1", effectiveFrom ?? Now, false, null);
        _legalDocuments.GetRequiredDocumentVersionsForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns([version]);
        return version.Id;
    }

    private static TeamInfo Team(Guid id, SystemTeamType type, Guid memberId, TeamMemberRole role) => new(
        id, "Team", null, "team", IsActive: true, IsSystemTeam: type != SystemTeamType.None, type,
        RequiresApproval: false, IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false, Now,
        [new TeamMemberInfo(Guid.NewGuid(), memberId, "Member", null, null, role, Now)]);

    [HumansFact]
    public async Task BatchCheck_ReturnsOnlyUsersWhoSignedEveryRequiredVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        var signer = Guid.NewGuid();
        var other = Guid.NewGuid();
        _signed[signer] = new HashSet<Guid> { Require(teamId) };

        (await _sut.GetUsersWithAllRequiredConsentsForTeamAsync([signer, other], teamId, ct))
            .Should().BeEquivalentTo([signer]);
        (await _sut.GetUsersWithAllRequiredConsentsForTeamAsync([signer, other], Guid.NewGuid(), ct))
            .Should().BeEquivalentTo([signer, other], "a team with no required documents admits everyone");
    }

    [HumansTheory]
    [InlineData(10, 0, true)]
    [InlineData(5, 0, false)]
    [InlineData(5, 3, true)]
    public async Task PastDeadline_AppliesTheGracePeriodAndTheReminderHorizon(
        int daysSinceEffective, int horizonDays, bool expected)
    {
        var unsigned = Guid.NewGuid();
        var signed = Guid.NewGuid();
        _roleAssignments.GetUserIdsWithActiveAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns([unsigned, signed]);
        _signed[signed] = new HashSet<Guid>
        {
            Require(SystemTeamIds.Volunteers, Now - Duration.FromDays(daysSinceEffective))
        };

        var result = await _sut.GetUsersPastConsentDeadlineAsync(
            Duration.FromDays(horizonDays), TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo(expected ? new[] { unsigned } : []);
    }

    [HumansFact]
    public async Task Progress_CountsVolunteersOwnTeamsAndCoordinatorsDocuments()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = Team(teamId, SystemTeamType.None, userId, TeamMemberRole.Coordinator)
        });
        var volunteers = Require(SystemTeamIds.Volunteers);
        var coordinators = Require(SystemTeamIds.Coordinators);
        var own = Require(teamId);
        _signed[userId] = new HashSet<Guid> { volunteers };

        var progress = await _sut.GetConsentProgressAsync(userId, TestContext.Current.CancellationToken);

        progress.RequiredCount.Should().Be(3);
        progress.MissingVersionIds.Should().BeEquivalentTo([coordinators, own]);
    }
}
