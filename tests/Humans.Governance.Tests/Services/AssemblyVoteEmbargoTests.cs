using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Governance.Tests.Infrastructure;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

using Xunit;

namespace Humans.Governance.Tests.Services;

/// <summary>
/// The embargo: no read path returns tally or ballot content for an Open vote except the
/// audited Admin peek; official and indicative results stay separate — see
/// <c>Docs/features/assembly-votes.md</c>'s Implementation checklist.
/// </summary>
public sealed class AssemblyVoteEmbargoTests : IDisposable
{
    private readonly AssemblyVoteServiceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [HumansFact]
    public async Task GetResultsAsync_WhileOpen_ReturnsNullForEveryone()
    {
        var vote = await _fx.AddVoteAsync();
        var roster = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        var memberView = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: false,
            Xunit.TestContext.Current.CancellationToken);
        var boardView = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: true,
            Xunit.TestContext.Current.CancellationToken);

        memberView.Should().BeNull();
        boardView.Should().BeNull("Board and Admin get no exemption from the embargo either");
    }

    [HumansFact]
    public async Task GetVoteForMemberAsync_WhileOpen_NeverExposesAnotherMembersBallot()
    {
        var vote = await _fx.AddVoteAsync();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var aliceRoster = await _fx.AddRosterRowAsync(vote.Id, alice, isOfficial: true);
        await _fx.AddRosterRowAsync(vote.Id, bob, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, aliceRoster.Id, AssemblyBallotChoice.Yes);

        var bobsView = await _fx.Service.GetVoteForMemberAsync(
            vote.Id, bob, Xunit.TestContext.Current.CancellationToken);

        bobsView!.OwnBallot.Should().BeNull("Bob has not voted; Alice's ballot must not leak into his view");
    }

    [HumansFact]
    public async Task GetBallotsForBoardAsync_WhileOpen_ReturnsNull()
    {
        var vote = await _fx.AddVoteAsync();
        var roster = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        var result = await _fx.Service.GetBallotsForBoardAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task PeekAsync_ReturnsTheLiveTally_WritesAuditAndPeekRow()
    {
        var vote = await _fx.AddVoteAsync();
        var r1 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var r2 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, r1.Id, AssemblyBallotChoice.Yes);
        await _fx.AddBallotAsync(vote.Id, r2.Id, AssemblyBallotChoice.No);
        var adminId = Guid.NewGuid();

        var (tally, recorded) = await _fx.Service.PeekAsync(
            vote.Id, adminId, Xunit.TestContext.Current.CancellationToken);

        tally.Should().NotBeNull();
        tally!.Official.YesNo.Should().Be(new YesNoTally(1, 1, 0));
        recorded.Should().BeTrue();

        var peeks = await _fx.Db.AssemblyVotePeeks
            .AsNoTracking()
            .Where(p => p.VoteId == vote.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        peeks.Should().ContainSingle(p => p.AdminUserId == adminId);

        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVotePeeked, Arg.Any<string>(), vote.Id, Arg.Any<string>(), adminId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task PeekAsync_OnClosedVote_DoesNotWriteAnotherPeekRow()
    {
        var vote = await _fx.AddVoteAsync();
        await _fx.Service.StopAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var (tally, recorded) = await _fx.Service.PeekAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        tally.Should().NotBeNull("a peek on a closed vote is just the results page");
        recorded.Should().BeFalse(
            "nothing was logged, so the caller must not render a page claiming a peek was recorded");
        (await _fx.Db.AssemblyVotePeeks
            .CountAsync(p => p.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(0);
    }

    [HumansFact]
    public async Task GetVoteForMemberAsync_WhileOpen_ExposesParticipationCountsOnly()
    {
        var vote = await _fx.AddVoteAsync();
        var r1 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var r2 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, r1.Id, AssemblyBallotChoice.Yes);

        var detail = await _fx.Service.GetVoteForMemberAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        detail!.Participation.OfficialRoster.Should().Be(2);
        detail.Participation.OfficialCast.Should().Be(1);
    }

    // ==========================================================================
    // Official / indicative separation
    // ==========================================================================

    [HumansFact]
    public async Task Results_OfficialAndIndicative_AreTalliedAndReportedSeparately()
    {
        var vote = await _fx.AddVoteAsync(indicativeAudience: IndicativeAudience.AllMembers);
        var official1 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var official2 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var indicative1 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: false);
        var indicative2 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: false);

        // Official: 2 Yes -> passes. Indicative: 2 No -> would fail, and must not affect the
        // official verdict or be merged into it.
        await _fx.AddBallotAsync(vote.Id, official1.Id, AssemblyBallotChoice.Yes);
        await _fx.AddBallotAsync(vote.Id, official2.Id, AssemblyBallotChoice.Yes);
        await _fx.AddBallotAsync(vote.Id, indicative1.Id, AssemblyBallotChoice.No);
        await _fx.AddBallotAsync(vote.Id, indicative2.Id, AssemblyBallotChoice.No);

        await _fx.Service.StopAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        var results = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: false, Xunit.TestContext.Current.CancellationToken);

        results.Should().NotBeNull();
        results!.Result.Official.Verdict.Should().Be(AssemblyVoteVerdict.Passed);
        results.Result.Official.YesNo.Should().Be(new YesNoTally(2, 0, 0));
        results.Result.Indicative.Should().NotBeNull();
        results.Result.Indicative!.Verdict.Should().Be(AssemblyVoteVerdict.Failed);
        results.Result.Indicative.YesNo.Should().Be(new YesNoTally(0, 2, 0));
    }

    [HumansFact]
    public async Task Results_NoIndicativeAudience_IndicativeResultIsNull()
    {
        var vote = await _fx.AddVoteAsync();
        var official = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, official.Id, AssemblyBallotChoice.Yes);

        await _fx.Service.StopAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        var results = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: false, Xunit.TestContext.Current.CancellationToken);

        results!.Result.Indicative.Should().BeNull();
    }

    [HumansFact]
    public async Task GetBallotsForBoardAsync_AfterClose_ReturnsBallotsAndAudits()
    {
        var vote = await _fx.AddVoteAsync();
        var roster = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);
        await _fx.Service.StopAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        var actorId = Guid.NewGuid();

        var rows = await _fx.Service.GetBallotsForBoardAsync(
            vote.Id, actorId, Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle();
        rows![0].Choice.Should().Be(AssemblyBallotChoice.Yes);
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyBallotsViewed, Arg.Any<string>(), vote.Id, Arg.Any<string>(), actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GetVotesForMemberAsync_NeverListsADraft()
    {
        await _fx.AddVoteAsync(AssemblyVoteStatus.Draft);
        await _fx.AddVoteAsync(AssemblyVoteStatus.Open);

        var list = await _fx.Service.GetVotesForMemberAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        list.Should().ContainSingle("a draft is the Board's authoring surface, not a vote members can see")
            .Which.Status.Should().Be(AssemblyVoteStatus.Open);
    }

    [HumansFact]
    public async Task GetVoteForMemberAsync_OnADraft_ReturnsNull()
    {
        var draft = await _fx.AddVoteAsync(AssemblyVoteStatus.Draft);

        var view = await _fx.Service.GetVoteForMemberAsync(
            draft.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        view.Should().BeNull("knowing a draft's id must not serve its official text");
    }

    [HumansFact]
    public async Task GetBallotsForBoardAsync_OnACancelledVote_ReturnsNull()
    {
        var vote = await _fx.AddVoteAsync(AssemblyVoteStatus.Cancelled);
        var roster = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        var result = await _fx.Service.GetBallotsForBoardAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull(
            "a cancelled vote's ballots are an abandoned record with no result — disclosure is for Closed only");
    }

    [HumansFact]
    public async Task GetAllForAdminAsync_ListsDrafts()
    {
        await _fx.AddVoteAsync(AssemblyVoteStatus.Draft);

        var list = await _fx.Service.GetAllForAdminAsync(Xunit.TestContext.Current.CancellationToken);

        list.Should().ContainSingle(
                "Create redirects here, and the Edit/Delete/Open controls are the only way "
                + "a draft is ever progressed")
            .Which.Status.Should().Be(AssemblyVoteStatus.Draft);
    }

    [HumansFact]
    public async Task Acta_NeverPrintsTheIndicativeTally()
    {
        var vote = await _fx.AddVoteAsync(indicativeAudience: IndicativeAudience.AllMembers);
        var official = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var indicative = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: false);
        await _fx.AddBallotAsync(vote.Id, official.Id, AssemblyBallotChoice.Yes);
        await _fx.AddBallotAsync(vote.Id, indicative.Id, AssemblyBallotChoice.No);
        await _fx.Service.StopAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var results = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: false,
            Xunit.TestContext.Current.CancellationToken);

        results!.Result.Indicative.Should().NotBeNull("the results page still shows it separately");
        results.ActaText.Should().NotContain(
            "Indicative",
            "the acta is the association's legal record of the binding vote");
    }

    [HumansFact]
    public async Task StopAsync_AuditsTheClosure_EvenWhenNotificationResolutionThrows()
    {
        var vote = await _fx.AddVoteAsync();
        _fx.NotificationResolve
            .ResolveBySourceKeyAsync(
                Arg.Any<NotificationSource>(), Arg.Any<string>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("notifications are down"));
        var adminId = Guid.NewGuid();

        await _fx.Service.StopAsync(vote.Id, adminId, Xunit.TestContext.Current.CancellationToken);

        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteStopped, Arg.Any<string>(), vote.Id, Arg.Any<string>(), adminId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CancelAsync_AuditsAndEmails_EvenWhenNotificationResolutionThrows()
    {
        var vote = await _fx.AddVoteAsync();
        _fx.NotificationResolve
            .ResolveBySourceKeyAsync(
                Arg.Any<NotificationSource>(), Arg.Any<string>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("notifications are down"));
        var adminId = Guid.NewGuid();

        var result = await _fx.Service.CancelAsync(
            vote.Id, "Called off", adminId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Ok);
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteCancelled, Arg.Any<string>(), vote.Id, Arg.Any<string>(), adminId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task StoredResult_RoundTripsEveryNestedRecord()
    {
        var vote = await _fx.AddVoteAsync();
        var r1 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var r2 = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, r1.Id, AssemblyBallotChoice.Yes);
        await _fx.AddBallotAsync(vote.Id, r2.Id, AssemblyBallotChoice.Abstain);

        await _fx.Service.StopAsync(vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        var results = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: false,
            Xunit.TestContext.Current.CancellationToken);

        // Read back out of ResultJson, so every nested record bound through its constructor
        // rather than being materialised with default fields.
        results!.Result.Official.RosterSize.Should().Be(2);
        results.Result.Official.BallotsCast.Should().Be(2);
        results.Result.Official.YesNo.Should().Be(new YesNoTally(1, 0, 1));
        results.Result.Method.Should().Be(AssemblyVoteMethod.YesNo);
    }

    [HumansFact]
    public async Task Acta_NamesTheAdminWhoStoppedTheVote()
    {
        var vote = await _fx.AddVoteAsync();
        await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);
        var adminId = Guid.NewGuid();
        _fx.StubActiveUsers(adminId);

        await _fx.Service.StopAsync(vote.Id, adminId, Xunit.TestContext.Current.CancellationToken);
        var results = await _fx.Service.GetResultsAsync(
            vote.Id, Guid.NewGuid(), viewerIsBoardOrAdmin: false,
            Xunit.TestContext.Current.CancellationToken);

        results!.ActaText.Should().Contain(
            "Member " + adminId,
            "the spec lists who closed the vote among the acta's contents");
    }

    [HumansFact]
    public async Task Detail_InAnUnauthoredCulture_IsNotLabelledATranslation()
    {
        // The fixture authors "en" only, and "en" is the official culture.
        var vote = await _fx.AddVoteAsync();

        var detail = await _fx.Service.GetVoteForMemberAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        detail!.IsTranslation.Should().BeFalse(
            "the viewer is being shown the binding official text, not a translation of it");
    }
}
