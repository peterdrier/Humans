using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Governance.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

using Xunit;

namespace Humans.Governance.Tests.Services;

/// <summary>
/// Ballot casting/changing, lifecycle transitions, the lapse sweep, and GDPR erasure — see
/// <c>Docs/features/assembly-votes.md</c>'s Implementation checklist.
/// </summary>
public sealed class AssemblyVoteServiceTests : IDisposable
{
    private readonly AssemblyVoteServiceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // ==========================================================================
    // Casting a ballot
    // ==========================================================================

    [HumansFact]
    public async Task CastBallotAsync_RosterMember_Records()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
        var ballot = await _fx.Db.AssemblyBallots
            .FirstAsync(b => b.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        ballot.Revision.Should().Be(1);
        ballot.Choice.Should().Be(AssemblyBallotChoice.Yes);
    }

    [HumansFact]
    public async Task CastBallotAsync_NonRosterMember_IsRejected()
    {
        var vote = await _fx.AddVoteAsync();

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, Guid.NewGuid(), AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.NotOnRoster);
        (await _fx.Db.AssemblyBallots.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task CastBallotAsync_ChangingABallot_BumpsRevisionAppendsHistoryAndKeepsOriginalCastAt()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);
        var firstCastAt = (await _fx.Db.AssemblyBallots
            .AsNoTracking()
            .FirstAsync(b => b.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken)).CastAt;

        _fx.Clock.AdvanceHours(1);
        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.No, null, Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
        var ballot = await _fx.Db.AssemblyBallots
            .AsNoTracking()
            .Include(b => b.History)
            .FirstAsync(b => b.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        ballot.Revision.Should().Be(2);
        ballot.Choice.Should().Be(AssemblyBallotChoice.No);
        ballot.CastAt.Should().Be(firstCastAt);
        ballot.UpdatedAt.Should().Be(_fx.Clock.GetCurrentInstant());
        ballot.History.Should().HaveCount(2);
        ballot.History.Select(h => h.Revision).Should().BeEquivalentTo([1, 2]);

        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyBallotCast,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyBallotChanged,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CastBallotAsync_AuditEntry_NamesVoterButNeverTheChoice()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        await _fx.Audit.Received().LogAsync(
            AuditAction.AssemblyBallotCast,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => !d.Contains("Yes", StringComparison.Ordinal)),
            userId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CastBallotAsync_VoteNotOpen_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.VoteNotOpen);
    }

    // ==========================================================================
    // Ballot validation
    // ==========================================================================

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_DuplicateKeysInRanking_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["a", "a"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_UnknownOptionKey_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["a", "nonexistent"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_YesNoVote_RankedChoice_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(kind: AssemblyVoteKind.YesNo);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["a"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_YesChoice_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_PartialRanking_IsAccepted()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1), ("c", 2)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["b"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
    }

    // ==========================================================================
    // Lifecycle: lapse without the job
    // ==========================================================================

    [HumansFact]
    public async Task Read_AfterClosesAtPasses_SettlesTheVoteClosedWithoutTheLapseJob()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        _fx.Clock.AdvanceMinutes(10);

        var detail = await _fx.Service.GetVoteForMemberAsync(
            vote.Id, userId, Xunit.TestContext.Current.CancellationToken);

        detail!.Status.Should().Be(AssemblyVoteStatus.Closed);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
        stored.ResultJson.Should().NotBeNull();
        stored.ClosedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task Read_TwiceAfterLapse_DoesNotRecomputeOrDoubleStoreTheResult()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        _fx.Clock.AdvanceMinutes(10);

        await _fx.Service.GetVoteForMemberAsync(vote.Id, userId, Xunit.TestContext.Current.CancellationToken);
        var firstResultJson = (await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken)).ResultJson;

        _fx.Clock.AdvanceMinutes(5);
        await _fx.Service.GetVoteForMemberAsync(vote.Id, userId, Xunit.TestContext.Current.CancellationToken);

        var secondResultJson = (await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken)).ResultJson;
        secondResultJson.Should().Be(firstResultJson);
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteClosed,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RunLapseAndReminderSweepAsync_ClosesLapsedVotes()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        _fx.Clock.AdvanceMinutes(10);

        var closedCount = await _fx.Service.RunLapseAndReminderSweepAsync(
            Xunit.TestContext.Current.CancellationToken);

        closedCount.Should().Be(1);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
    }

    // ==========================================================================
    // Terminal states
    // ==========================================================================

    [HumansFact]
    public async Task StopAsync_AlreadyClosed_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.StopAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task StopAsync_AlreadyCancelled_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Cancelled);

        var result = await _fx.Service.StopAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task CancelAsync_AlreadyClosed_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.CancelAsync(
            vote.Id, "reason", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task ExtendAsync_AlreadyClosed_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.ExtendAsync(
            vote.Id, _fx.Clock.GetCurrentInstant() + Duration.FromDays(2), Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task CastBallotAsync_OnCancelledVote_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Cancelled);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.VoteNotOpen);
    }

    [HumansFact]
    public async Task OpenAsync_NonDraftVote_ReturnsWrongStateAndNeverReopens()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    // ==========================================================================
    // GDPR erasure
    // ==========================================================================

    [HumansFact]
    public async Task EraseForUserAsync_TombstonesRosterRow_AndLeavesTheStoredResultIntact()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        var userId = Guid.NewGuid();
        var roster = await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        // Close the vote for real so it carries a stored result to protect.
        _fx.Db.ChangeTracker.Clear();
        var stored = await _fx.Db.AssemblyVotes.FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ResultJson = "{\"official\":{}}";
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        await _fx.Service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        var rosterAfter = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .FirstAsync(r => r.Id == roster.Id, Xunit.TestContext.Current.CancellationToken);
        rosterAfter.UserId.Should().BeNull();

        var voteAfter = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        voteAfter.ResultJson.Should().Be("{\"official\":{}}");

        var ballotAfter = await _fx.Db.AssemblyBallots
            .AsNoTracking()
            .FirstAsync(b => b.RosterId == roster.Id, Xunit.TestContext.Current.CancellationToken);
        ballotAfter.Choice.Should().Be(AssemblyBallotChoice.Yes);
    }

    [HumansFact]
    public void ErasureDeclaration_DeclaresPartialRetentionForAssemblyVotes()
    {
        _fx.Service.ErasureDeclaration.Should().ContainKey(
            GdprExportSections.AssemblyVotes);
    }

    // ==========================================================================
    // An elapsed vote is closed for every purpose, admin actions included
    // ==========================================================================

    [HumansFact]
    public async Task CancelAsync_AfterClosesAtPasses_IsRejectedAndTheStoredResultSurvives()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        var roster = await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        _fx.Clock.AdvanceMinutes(10);

        var result = await _fx.Service.CancelAsync(
            vote.Id, "changed our minds", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
        stored.ResultJson.Should().NotBeNull();
        stored.CancelReason.Should().BeNull();
    }

    [HumansFact]
    public async Task StopAsync_AfterClosesAtPasses_IsRejectedAndTheLapseIsNotAttributedToTheAdmin()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var adminId = Guid.NewGuid();

        _fx.Clock.AdvanceMinutes(10);

        var result = await _fx.Service.StopAsync(
            vote.Id, adminId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
        stored.ClosedByUserId.Should().BeNull();
    }

    // ==========================================================================
    // A ranked draft can be edited more than once
    // ==========================================================================

    [HumansFact]
    public async Task UpdateDraftAsync_OnARankedDraft_ReplacesTheOptions()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft,
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);

        var result = await _fx.Service.UpdateDraftAsync(
            vote.Id,
            _fx.DraftFor(vote, AssemblyVoteKind.RankedChoice, ["c", "d"]),
            Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Ok);
        var keys = await _fx.Db.AssemblyVoteOptions
            .AsNoTracking()
            .Where(o => o.VoteId == vote.Id)
            .Select(o => o.Key)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        keys.Should().BeEquivalentTo(["c", "d"]);
    }
}
