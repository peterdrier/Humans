using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Governance.Tests.Infrastructure;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

using Xunit;

namespace Humans.Governance.Tests.Services;

/// <summary>
/// The roster snapshot taken at <c>Open</c>: who ends up official vs. indicative, dedup
/// across sources, exclusion of inactive accounts, and that it is never recomputed — see
/// <c>Docs/features/assembly-votes.md</c>'s Implementation checklist.
/// </summary>
public sealed class AssemblyVoteRosterTests : IDisposable
{
    private readonly AssemblyVoteServiceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [HumansFact]
    public async Task OpenAsync_Roster_IncludesActiveAsociadosAndBoardMembersAsOfficial()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        var boardMember = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));
        _fx.RoleAssignments.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([boardMember]));
        _fx.StubActiveUsers(asociado, boardMember);

        var result = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Ok);
        var roster = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .Where(r => r.VoteId == vote.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        roster.Should().HaveCount(2);
        roster.Should().OnlyContain(r => r.IsOfficial);
        roster.Single(r => r.UserId == boardMember).IsBoardMember.Should().BeTrue();
    }

    [HumansFact]
    public async Task OpenAsync_PersonBothAsociadoAndBoardMember_GetsExactlyOneOfficialRow()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var person = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([person]));
        _fx.RoleAssignments.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([person]));
        _fx.StubActiveUsers(person);

        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var roster = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .Where(r => r.VoteId == vote.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        roster.Should().ContainSingle(r => r.UserId == person);
        roster.Single().IsOfficial.Should().BeTrue();
    }

    [HumansFact]
    public async Task OpenAsync_ColaboradorAudience_AddsColaboradoresAsIndicativeOnly()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft, indicativeAudience: IndicativeAudience.Colaboradores);
        var colaborador = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Colaborador, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([colaborador]));
        _fx.StubActiveUsers(colaborador);

        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var row = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .SingleAsync(r => r.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        row.IsOfficial.Should().BeFalse();
    }

    [HumansFact]
    public async Task OpenAsync_AllMembersAudience_AddsVolunteersAsIndicative()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft, indicativeAudience: IndicativeAudience.AllMembers);
        var volunteer = Guid.NewGuid();
        _fx.Teams.GetTeamAsync(SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TeamInfo?>(new TeamInfo(
                SystemTeamIds.Volunteers, "Volunteers", null, "volunteers",
                true, true, SystemTeamType.Volunteers, false, false, false, false,
                _fx.Clock.GetCurrentInstant(),
                [new TeamMemberInfo(Guid.NewGuid(), volunteer, "Volunteer", null, null,
                    TeamMemberRole.Member, _fx.Clock.GetCurrentInstant())])));
        _fx.StubActiveUsers(volunteer);

        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var row = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .SingleAsync(r => r.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        row.UserId.Should().Be(volunteer);
        row.IsOfficial.Should().BeFalse();
    }

    [HumansFact]
    public async Task OpenAsync_IndicativeAudienceNone_NoVolunteersOrColaboradoresAdded()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));
        _fx.StubActiveUsers(asociado);

        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _fx.Teams.DidNotReceive().GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OpenAsync_InactiveCandidate_IsExcludedFromTheRoster()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));
        // Deliberately not stubbed active: GetUserInfosAsync returns the fixture's default
        // empty map, so the candidate has no known Active state and is dropped.

        var result = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        // No survivors at all makes an empty roster, which OpenAsync itself refuses.
        result.Should().Be(AssemblyVoteActionResult.Invalid);
        (await _fx.Db.AssemblyVoteRosterEntries
            .CountAsync(r => r.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    [HumansFact]
    public async Task OpenAsync_SuspendedCandidateAmongOthers_IsExcludedButOthersSurvive()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var active = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([active, suspended]));
        var map = new Dictionary<Guid, UserInfo>
        {
            [active] = new User
            {
                Id = active,
                DisplayName = "Active",
                UserName = "a@x.com",
                Email = "a@x.com",
                PreferredLanguage = "en",
                State = UserState.Active
            }.ToUserInfo(),
            [suspended] = new User
            {
                Id = suspended,
                DisplayName = "Suspended",
                UserName = "s@x.com",
                Email = "s@x.com",
                PreferredLanguage = "en",
                State = UserState.Suspended
            }.ToUserInfo()
        };
        _fx.Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(map));

        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var roster = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .Where(r => r.VoteId == vote.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        roster.Should().ContainSingle(r => r.UserId == active);
        roster.Should().NotContain(r => r.UserId == suspended);
    }

    [HumansFact]
    public async Task Roster_IsNeverRecomputed_LateAsociadoIsNotOnItAndCannotVote()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var earlyAsociado = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([earlyAsociado]));
        _fx.StubActiveUsers(earlyAsociado);
        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        // A member becomes Asociado only after the vote already opened.
        var lateAsociado = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([earlyAsociado, lateAsociado]));
        _fx.StubActiveUsers(earlyAsociado, lateAsociado);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, lateAsociado, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.NotOnRoster);
        (await _fx.Db.AssemblyVoteRosterEntries
            .CountAsync(r => r.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task Roster_IsNeverRecomputed_MemberWhoLostTermMidVoteKeepsTheirBallot()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));
        _fx.StubActiveUsers(asociado);
        await _fx.Service.OpenAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        // Their term now expires; the roster row does not know or care.
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, asociado, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
    }
}
