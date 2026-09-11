using AwesomeAssertions;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;

using Xunit;

namespace Humans.Governance.Tests.Services;

using CountableBallot = AssemblyVoteCounting.CountableBallot;

/// <summary>
/// The pure counting rules: YesNo majorities and instant-runoff, with no clock and no
/// repository — see <c>Docs/features/assembly-votes.md</c>'s Implementation checklist.
/// </summary>
public sealed class AssemblyVoteCountingTests
{
    // ==========================================================================
    // YesNo — Simple majority
    // ==========================================================================

    [HumansFact]
    public void CountYesNo_Simple_MoreYesThanNo_Passes()
    {
        var ballots = Ballots(Yes, Yes, Yes, No);

        var (tally, verdict) = AssemblyVoteCounting.CountYesNo(ballots, RequiredMajority.Simple);

        tally.Should().Be(new YesNoTally(3, 1, 0));
        verdict.Should().Be(AssemblyVoteVerdict.Passed);
    }

    [HumansFact]
    public void CountYesNo_Simple_MoreNoThanYes_Fails()
    {
        var ballots = Ballots(Yes, No, No, No);

        var (_, verdict) = AssemblyVoteCounting.CountYesNo(ballots, RequiredMajority.Simple);

        verdict.Should().Be(AssemblyVoteVerdict.Failed);
    }

    [HumansFact]
    public void CountYesNo_Simple_ExactTie_IsReportedAsTie()
    {
        var ballots = Ballots(Yes, Yes, No, No, Abstain);

        var (tally, verdict) = AssemblyVoteCounting.CountYesNo(ballots, RequiredMajority.Simple);

        tally.Should().Be(new YesNoTally(2, 2, 1));
        verdict.Should().Be(AssemblyVoteVerdict.Tie);
    }

    // ==========================================================================
    // YesNo — Two-thirds majority, exact boundaries
    // ==========================================================================

    // xUnit needs the test class and its signatures public, and AssemblyVoteVerdict is
    // internal to the section, so the expectation travels as a bool rather than the enum.
    [HumansTheory]
    // ceil(2/3 * 3) = 2
    [InlineData(2, 1, true)]
    [InlineData(1, 2, false)]
    // ceil(2/3 * 6) = 4
    [InlineData(4, 2, true)]
    [InlineData(3, 3, false)] // just below the 4/6 threshold
    // ceil(2/3 * 7) = 5
    [InlineData(5, 2, true)]
    [InlineData(4, 3, false)] // just below the 5/7 threshold
    public void CountYesNo_TwoThirds_BoundaryCases(int yes, int no, bool expectedToPass)
    {
        var ballots = Ballots([.. Enumerable.Repeat(Yes, yes), .. Enumerable.Repeat(No, no)]);

        var (_, verdict) = AssemblyVoteCounting.CountYesNo(ballots, RequiredMajority.TwoThirds);

        verdict.Should().Be(expectedToPass ? AssemblyVoteVerdict.Passed : AssemblyVoteVerdict.Failed);
    }

    [HumansFact]
    public void CountYesNo_TwoThirds_AbstainsExcludedFromBase()
    {
        // 2 of 3 cast (excluding two abstentions) reaches ceil(2/3*3) = 2.
        var ballots = Ballots(Yes, Yes, No, Abstain, Abstain);

        var (tally, verdict) = AssemblyVoteCounting.CountYesNo(ballots, RequiredMajority.TwoThirds);

        tally.Abstain.Should().Be(2);
        verdict.Should().Be(AssemblyVoteVerdict.Passed);
    }

    // ==========================================================================
    // Zero ballots
    // ==========================================================================

    [HumansFact]
    public void CountYesNo_ZeroBallots_Simple_IsTie()
    {
        var (_, verdict) = AssemblyVoteCounting.CountYesNo([], RequiredMajority.Simple);

        verdict.Should().Be(AssemblyVoteVerdict.Tie);
    }

    [HumansFact]
    public void CountYesNo_ZeroBallots_TwoThirds_Fails()
    {
        var (_, verdict) = AssemblyVoteCounting.CountYesNo([], RequiredMajority.TwoThirds);

        verdict.Should().Be(AssemblyVoteVerdict.Failed);
    }

    // ==========================================================================
    // Instant-runoff
    // ==========================================================================

    [HumansFact]
    public void CountInstantRunoff_OutrightMajority_WinsInOneRound()
    {
        var options = Options(("a", 0), ("b", 1));
        var ballots = Ballots(
            Ranked("a"), Ranked("a"), Ranked("a"), Ranked("b"));

        var (rounds, winner, verdict, _) = AssemblyVoteCounting.CountInstantRunoff(ballots, options);

        rounds.Should().HaveCount(1);
        winner.Should().Be("a");
        verdict.Should().Be(AssemblyVoteVerdict.Passed);
    }

    [HumansFact]
    public void CountInstantRunoff_MultiRound_EliminatesAndExhaustedBallotsShrinkTheBase()
    {
        var options = Options(("a", 0), ("b", 1), ("c", 2));
        // Round 1 first preferences: a=3, b=2, c=4 (total 9) — nobody has a majority.
        // b is lowest and is eliminated; its 2 ballots rank nothing else, so they exhaust.
        // Round 2 continuing base is 7 (9 minus the 2 exhausted), and c's 4 clears >50% of 7.
        var ballots = Ballots(
            Ranked("a"), Ranked("a"), Ranked("a"),
            Ranked("b"), Ranked("b"),
            Ranked("c", "a"), Ranked("c", "a"), Ranked("c", "a"), Ranked("c", "a"));

        var (rounds, winner, verdict, _) = AssemblyVoteCounting.CountInstantRunoff(ballots, options);

        rounds.Should().HaveCount(2);
        rounds[0].EliminatedKey.Should().Be("b");
        rounds[0].Exhausted.Should().Be(0);
        rounds[1].Exhausted.Should().Be(2);
        rounds[1].Counts["c"].Should().Be(4);
        rounds[1].Counts["a"].Should().Be(3);
        winner.Should().Be("c");
        verdict.Should().Be(AssemblyVoteVerdict.Passed);
    }

    [HumansFact]
    public void CountInstantRunoff_TieBreak_FewestVotesInPreviousRound()
    {
        var options = Options(("a", 0), ("b", 1), ("c", 2), ("d", 3));
        // Round 1: a=4, b=3, c=2, d=1 — d is eliminated outright (no tie).
        // Round 2: a=4, b=3, c=3 (the d,c ballot now counts for c) — b and c tie at 3.
        // Round-1 counts break the tie: c had fewer (2) than b (3), so c is eliminated.
        var ballots = Ballots(
            Ranked("a"), Ranked("a"), Ranked("a"), Ranked("a"),
            Ranked("b", "c"), Ranked("b", "c"), Ranked("b", "c"),
            Ranked("c", "b"), Ranked("c", "b"),
            Ranked("d", "c"));

        var (rounds, winner, verdict, notes) = AssemblyVoteCounting.CountInstantRunoff(ballots, options);

        rounds.Should().HaveCount(3);
        rounds[0].EliminatedKey.Should().Be("d");
        rounds[1].EliminatedKey.Should().Be("c");
        notes.Should().ContainSingle()
            .Which.Should().Be(new AssemblyVoteNote(AssemblyVoteNoteKind.TieBrokenByPreviousRound, 2, 2));
        winner.Should().Be("b");
        verdict.Should().Be(AssemblyVoteVerdict.Passed);
    }

    [HumansFact]
    public void CountInstantRunoff_TieBreak_AuthoredOrderWhenPreviousRoundAlsoLevel()
    {
        var options = Options(("a", 0), ("b", 1), ("c", 2));
        // Round 1 (no previous round to break by): a=2, b=1, c=1 — b and c tie for fewest,
        // and there is no earlier round to break the tie, so authored order decides: b
        // (order 1) comes before c (order 2) and is eliminated.
        var ballots = Ballots(Ranked("a"), Ranked("a"), Ranked("b"), Ranked("c"));

        var (rounds, _, _, notes) = AssemblyVoteCounting.CountInstantRunoff(ballots, options);

        rounds[0].EliminatedKey.Should().Be("b");
        notes.Should().ContainSingle()
            .Which.Should().Be(new AssemblyVoteNote(AssemblyVoteNoteKind.TieBrokenByAuthoredOrder, 1, 2));
    }

    [HumansFact]
    public void CountInstantRunoff_LevelDecidingRoundWithTwoOptions_IsTieNotElimination()
    {
        var options = Options(("a", 0), ("b", 1));
        var ballots = Ballots(Ranked("a"), Ranked("b"));

        var (rounds, winner, verdict, notes) = AssemblyVoteCounting.CountInstantRunoff(ballots, options);

        rounds.Should().HaveCount(1);
        rounds[0].EliminatedKey.Should().BeNull();
        rounds[0].WinnerKey.Should().BeNull();
        winner.Should().BeNull();
        verdict.Should().Be(AssemblyVoteVerdict.Tie);
        notes.Should().ContainSingle()
            .Which.Should().Be(new AssemblyVoteNote(AssemblyVoteNoteKind.DecidingRoundLevel, 1, 2));
    }

    [HumansFact]
    public void CountInstantRunoff_AbstainBallots_AreExhaustedFromTheStart()
    {
        var options = Options(("a", 0), ("b", 1));
        var ballots = Ballots(Ranked("a"), Ranked("a"), Ranked("b"), new CountableBallot(Abstain, null));

        var (rounds, winner, verdict, _) = AssemblyVoteCounting.CountInstantRunoff(ballots, options);

        rounds[0].Exhausted.Should().Be(1);
        winner.Should().Be("a");
        verdict.Should().Be(AssemblyVoteVerdict.Passed);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private const AssemblyBallotChoice Yes = AssemblyBallotChoice.Yes;
    private const AssemblyBallotChoice No = AssemblyBallotChoice.No;
    private const AssemblyBallotChoice Abstain = AssemblyBallotChoice.Abstain;

    private static List<CountableBallot> Ballots(params AssemblyBallotChoice[] choices) =>
        choices.Select(c => new CountableBallot(c, null)).ToList();

    private static List<CountableBallot> Ballots(params CountableBallot[] ballots) =>
        [.. ballots];

    private static CountableBallot Ranked(params string[] ranking) =>
        new(AssemblyBallotChoice.Ranked, ranking);

    private static List<AssemblyVoteOption> Options(params (string Key, int Order)[] options) =>
        options
            .Select(o => new AssemblyVoteOption
            {
                Id = Guid.NewGuid(),
                VoteId = Guid.NewGuid(),
                Key = o.Key,
                Order = o.Order,
                Label = GovernanceLocalizedText.Empty
            })
            .ToList();
}
