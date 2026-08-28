using AwesomeAssertions;
using Humans.Surveys.Services;

namespace Humans.Surveys.Tests.Services;

public class RankedChoiceCounterTests
{
    private static readonly IReadOnlyList<string> Candidates = ["A", "B", "C"];

    [HumansFact]
    public void Pairwise_treats_ranked_as_better_than_unranked_and_rejected()
    {
        var matrix = RankedChoiceCounter.BuildPairwise(
            Candidates,
            [Ballot([["A"]], "C")]);

        Contest(matrix, "A", "B").Should().Be((1, 0));
        Contest(matrix, "A", "C").Should().Be((1, 0));
        Contest(matrix, "B", "C").Should().Be((1, 0));
    }

    [HumansFact]
    public void Pairwise_records_no_preference_inside_equal_rank_or_bottom_tier()
    {
        var matrix = RankedChoiceCounter.BuildPairwise(
            ["A", "B", "C", "D"],
            [Ballot([["A", "B"]], "C", "D")]);

        Contest(matrix, "A", "B").Should().Be((0, 0));
        Contest(matrix, "C", "D").Should().Be((0, 0));
    }

    [HumansFact]
    public void Ranked_pairs_resolves_a_condorcet_cycle()
    {
        var ballots = Repeat(3, Ballot([["A"], ["B"], ["C"]]))
            .Concat(Repeat(2, Ballot([["B"], ["C"], ["A"]])))
            .Concat(Repeat(2, Ballot([["C"], ["A"], ["B"]])))
            .ToList();
        var matrix = RankedChoiceCounter.BuildPairwise(Candidates, ballots);

        var result = RankedChoiceCounter.CountRankedPairs(Candidates, matrix);

        result.Winner.Should().Be("A");
        result.Locks.Should().ContainEquivalentOf(new RankedPairsLock("C", "A", 1, 4, false));
    }

    [HumansFact]
    public void Ranked_pairs_uses_authored_pair_order_for_equal_strength_victories()
    {
        var matrix = new RankedPairwiseMatrix(
        [
            new PairwiseContest("A", "B", 3, 1),
            new PairwiseContest("A", "C", 1, 3),
            new PairwiseContest("B", "C", 3, 1),
        ]);

        var result = RankedChoiceCounter.CountRankedPairs(Candidates, matrix);

        result.Locks.Select(item => (item.Winner, item.Loser)).Should().Equal(
            ("A", "B"),
            ("B", "C"),
            ("C", "A"));
        result.TieBreakUsed.Should().BeTrue();
    }

    [HumansFact]
    public void Condorcet_returns_the_candidate_who_beats_every_other_candidate()
    {
        var matrix = RankedChoiceCounter.BuildPairwise(
            Candidates,
            [
                Ballot([["A"], ["B"], ["C"]]),
                Ballot([["A"], ["C"], ["B"]]),
                Ballot([["B"], ["A"], ["C"]]),
            ]);

        var result = RankedChoiceCounter.CheckCondorcet(Candidates, matrix);

        result.Winner.Should().Be("A");
        result.SmallestCycle.Should().BeEmpty();
    }

    [HumansFact]
    public void Condorcet_reports_the_smallest_visible_cycle()
    {
        var matrix = new RankedPairwiseMatrix(
        [
            new PairwiseContest("A", "B", 2, 1),
            new PairwiseContest("A", "C", 1, 2),
            new PairwiseContest("B", "C", 2, 1),
        ]);

        var result = RankedChoiceCounter.CheckCondorcet(Candidates, matrix);

        result.Winner.Should().BeNull();
        result.SmallestCycle.Should().HaveCount(4);
        result.SmallestCycle[0].Should().Be(result.SmallestCycle[^1]);
    }

    [HumansFact]
    public void Borda_averages_every_tied_tier_and_keeps_reject_below_unranked()
    {
        var result = RankedChoiceCounter.CountBorda(
            ["A", "B", "C", "D", "E"],
            [Ballot([["A"], ["B", "C"]], "E")]);

        Score(result, "A").Should().Be("4");
        Score(result, "B").Should().Be("5/2");
        Score(result, "C").Should().Be("5/2");
        Score(result, "D").Should().Be("1");
        Score(result, "E").Should().Be("0");
    }

    [HumansFact]
    public void Availability_removes_candidates_without_mutating_ballots()
    {
        var ballots = new[]
        {
            Ballot([["A"], ["B"], ["C"]]),
            Ballot([["B"], ["C"], ["A"]]),
        };
        var active = new HashSet<string>(["A", "C"], StringComparer.Ordinal);

        var matrix = RankedChoiceCounter.BuildPairwise(Candidates, ballots, active);
        var borda = RankedChoiceCounter.CountBorda(Candidates, ballots, active);

        matrix.Contests.Should().ContainSingle();
        matrix.Contests[0].First.Should().Be("A");
        matrix.Contests[0].Second.Should().Be("C");
        borda.Scores.Select(score => score.Candidate).Should().Equal("A", "C");
        ballots[0].RankGroups.SelectMany(group => group).Should().Contain("B");
    }

    [HumansFact]
    public void Empty_and_single_candidate_counts_are_defined()
    {
        RankedChoiceCounter.CountRankedPairs(
                [],
                new RankedPairwiseMatrix([]))
            .Winner.Should().BeNull();

        var matrix = RankedChoiceCounter.BuildPairwise(["A"], []);
        RankedChoiceCounter.CountRankedPairs(["A"], matrix).Winner.Should().Be("A");
        RankedChoiceCounter.CheckCondorcet(["A"], matrix).Winner.Should().Be("A");
        RankedChoiceCounter.CountBorda(["A"], []).Winner.Should().Be("A");
    }

    private static RankedBallot Ballot(
        IReadOnlyList<IReadOnlyList<string>> groups,
        params string[] rejected) =>
        new(groups, rejected.ToHashSet(StringComparer.Ordinal));

    private static IEnumerable<RankedBallot> Repeat(int count, RankedBallot ballot) =>
        Enumerable.Repeat(ballot, count);

    private static (int First, int Second) Contest(
        RankedPairwiseMatrix matrix,
        string first,
        string second)
    {
        var contest = matrix.Contests.Single(item =>
            string.Equals(item.First, first, StringComparison.Ordinal) &&
            string.Equals(item.Second, second, StringComparison.Ordinal));
        return (contest.PreferFirst, contest.PreferSecond);
    }

    private static string Score(BordaResult result, string candidate) =>
        result.Scores
            .Single(score => string.Equals(score.Candidate, candidate, StringComparison.Ordinal))
            .Score
            .ToString();
}
