using System.Numerics;

namespace Humans.Surveys.Services;

internal sealed record RankedBallot(
    IReadOnlyList<IReadOnlyList<string>> RankGroups,
    IReadOnlySet<string> Rejected);

internal sealed record PairwiseContest(
    string First,
    string Second,
    int PreferFirst,
    int PreferSecond);

internal sealed record RankedPairwiseMatrix(IReadOnlyList<PairwiseContest> Contests);

internal sealed record RankedPairsLock(
    string Winner,
    string Loser,
    int Margin,
    int WinningVotes,
    bool Locked);

internal sealed record RankedPairsResult(
    string? Winner,
    IReadOnlyList<RankedPairsLock> Locks,
    bool TieBreakUsed);

internal sealed record CondorcetResult(
    string? Winner,
    IReadOnlyList<string> SmallestCycle);

internal readonly record struct RankedFraction : IComparable<RankedFraction>
{
    public static RankedFraction Zero => new(BigInteger.Zero, BigInteger.One);

    public RankedFraction(BigInteger numerator, BigInteger denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }

    public static RankedFraction operator +(RankedFraction left, RankedFraction right) =>
        new(
            left.Numerator * right.Denominator + right.Numerator * left.Denominator,
            left.Denominator * right.Denominator);

    public static bool operator <(RankedFraction left, RankedFraction right) =>
        left.CompareTo(right) < 0;

    public static bool operator >(RankedFraction left, RankedFraction right) =>
        left.CompareTo(right) > 0;

    public static bool operator <=(RankedFraction left, RankedFraction right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(RankedFraction left, RankedFraction right) =>
        left.CompareTo(right) >= 0;

    public int CompareTo(RankedFraction other) =>
        (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    public override string ToString() =>
        Denominator == BigInteger.One ? Numerator.ToString() : $"{Numerator}/{Denominator}";
}

internal sealed record BordaScore(string Candidate, RankedFraction Score);

internal sealed record BordaResult(
    string? Winner,
    IReadOnlyList<BordaScore> Scores,
    bool TieBreakUsed);

internal static class RankedChoiceCounter
{
    public static RankedPairwiseMatrix BuildPairwise(
        IReadOnlyList<string> authoredCandidates,
        IReadOnlyList<RankedBallot> ballots,
        IReadOnlySet<string>? activeCandidates = null)
    {
        var candidates = GetCandidates(authoredCandidates, activeCandidates);
        var preferences = ballots.Select(ballot => BuildPreferences(ballot, candidates)).ToList();
        var contests = new List<PairwiseContest>();

        for (var first = 0; first < candidates.Count; first++)
        {
            for (var second = first + 1; second < candidates.Count; second++)
            {
                var preferFirst = 0;
                var preferSecond = 0;

                foreach (var preference in preferences)
                {
                    var comparison = preference[candidates[first]].CompareTo(preference[candidates[second]]);
                    if (comparison < 0)
                    {
                        preferFirst++;
                    }
                    else if (comparison > 0)
                    {
                        preferSecond++;
                    }
                }

                contests.Add(new PairwiseContest(
                    candidates[first],
                    candidates[second],
                    preferFirst,
                    preferSecond));
            }
        }

        return new RankedPairwiseMatrix(contests);
    }

    public static RankedPairsResult CountRankedPairs(
        IReadOnlyList<string> authoredCandidates,
        RankedPairwiseMatrix matrix,
        IReadOnlySet<string>? activeCandidates = null)
    {
        var candidates = GetCandidates(authoredCandidates, activeCandidates);
        if (candidates.Count == 0)
        {
            return new RankedPairsResult(null, [], false);
        }

        var order = candidates
            .Select((candidate, index) => (candidate, index))
            .ToDictionary(item => item.candidate, item => item.index, StringComparer.Ordinal);
        var victories = matrix.Contests
            .Where(contest => order.ContainsKey(contest.First) && order.ContainsKey(contest.Second))
            .Where(contest => contest.PreferFirst != contest.PreferSecond)
            .Select(contest => contest.PreferFirst > contest.PreferSecond
                ? new Victory(
                    contest.First,
                    contest.Second,
                    contest.PreferFirst - contest.PreferSecond,
                    contest.PreferFirst)
                : new Victory(
                    contest.Second,
                    contest.First,
                    contest.PreferSecond - contest.PreferFirst,
                    contest.PreferSecond))
            .OrderByDescending(victory => victory.Margin)
            .ThenByDescending(victory => victory.WinningVotes)
            .ThenBy(victory => order[victory.Winner])
            .ThenBy(victory => order[victory.Loser])
            .ToList();

        var tieBreakUsed = victories
            .GroupBy(victory => (victory.Margin, victory.WinningVotes))
            .Any(group => group.Count() > 1);
        var edges = candidates.ToDictionary(
            candidate => candidate,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var locks = new List<RankedPairsLock>();

        foreach (var victory in victories)
        {
            var locked = !HasPath(edges, victory.Loser, victory.Winner);
            if (locked)
            {
                edges[victory.Winner].Add(victory.Loser);
            }

            locks.Add(new RankedPairsLock(
                victory.Winner,
                victory.Loser,
                victory.Margin,
                victory.WinningVotes,
                locked));
        }

        var destinations = edges.Values.SelectMany(values => values).ToHashSet(StringComparer.Ordinal);
        var sources = candidates.Where(candidate => !destinations.Contains(candidate)).ToList();
        tieBreakUsed |= sources.Count > 1;

        return new RankedPairsResult(sources[0], locks, tieBreakUsed);
    }

    public static CondorcetResult CheckCondorcet(
        IReadOnlyList<string> authoredCandidates,
        RankedPairwiseMatrix matrix,
        IReadOnlySet<string>? activeCandidates = null)
    {
        var candidates = GetCandidates(authoredCandidates, activeCandidates);
        var edges = candidates.ToDictionary(
            candidate => candidate,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var contest in matrix.Contests)
        {
            if (!edges.ContainsKey(contest.First) || !edges.ContainsKey(contest.Second))
            {
                continue;
            }

            if (contest.PreferFirst > contest.PreferSecond)
            {
                edges[contest.First].Add(contest.Second);
            }
            else if (contest.PreferSecond > contest.PreferFirst)
            {
                edges[contest.Second].Add(contest.First);
            }
        }

        var winner = candidates.FirstOrDefault(candidate =>
            candidates.All(other =>
                string.Equals(candidate, other, StringComparison.Ordinal) ||
                edges[candidate].Contains(other)));

        return new CondorcetResult(winner, winner is null ? FindSmallestCycle(candidates, edges) : []);
    }

    public static BordaResult CountBorda(
        IReadOnlyList<string> authoredCandidates,
        IReadOnlyList<RankedBallot> ballots,
        IReadOnlySet<string>? activeCandidates = null)
    {
        var candidates = GetCandidates(authoredCandidates, activeCandidates);
        if (candidates.Count == 0)
        {
            return new BordaResult(null, [], false);
        }

        var totals = candidates.ToDictionary(
            candidate => candidate,
            _ => RankedFraction.Zero,
            StringComparer.Ordinal);

        foreach (var ballot in ballots)
        {
            var groups = BuildBordaGroups(ballot, candidates);
            var position = 0;

            foreach (var group in groups)
            {
                var pointSum = BigInteger.Zero;
                for (var offset = 0; offset < group.Count; offset++)
                {
                    pointSum += candidates.Count - 1 - (position + offset);
                }

                var score = new RankedFraction(pointSum, group.Count);
                foreach (var candidate in group)
                {
                    totals[candidate] += score;
                }

                position += group.Count;
            }
        }

        var order = candidates
            .Select((candidate, index) => (candidate, index))
            .ToDictionary(item => item.candidate, item => item.index, StringComparer.Ordinal);
        var scores = totals
            .Select(item => new BordaScore(item.Key, item.Value))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => order[item.Candidate])
            .ToList();
        var tieBreakUsed = scores
            .GroupBy(score => score.Score)
            .Any(group => group.Count() > 1);

        return new BordaResult(scores[0].Candidate, scores, tieBreakUsed);
    }

    private static IReadOnlyList<string> GetCandidates(
        IReadOnlyList<string> authoredCandidates,
        IReadOnlySet<string>? activeCandidates)
    {
        if (authoredCandidates.Count != authoredCandidates.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Candidate values must be unique.", nameof(authoredCandidates));
        }

        return authoredCandidates
            .Where(candidate => activeCandidates is null || activeCandidates.Contains(candidate))
            .ToList();
    }

    private static Dictionary<string, int> BuildPreferences(
        RankedBallot ballot,
        IReadOnlyList<string> candidates)
    {
        var candidateSet = candidates.ToHashSet(StringComparer.Ordinal);
        var preferences = new Dictionary<string, int>(StringComparer.Ordinal);
        var rank = 0;

        foreach (var group in ballot.RankGroups)
        {
            var activeGroup = group.Where(candidateSet.Contains).Distinct(StringComparer.Ordinal).ToList();
            foreach (var candidate in activeGroup)
            {
                preferences.TryAdd(candidate, rank);
            }

            if (activeGroup.Count > 0)
            {
                rank++;
            }
        }

        var unrankedRank = rank;
        var rejectedRank = rank + 1;
        foreach (var candidate in candidates)
        {
            if (!preferences.ContainsKey(candidate))
            {
                preferences[candidate] = ballot.Rejected.Contains(candidate)
                    ? rejectedRank
                    : unrankedRank;
            }
        }

        return preferences;
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildBordaGroups(
        RankedBallot ballot,
        IReadOnlyList<string> candidates)
    {
        var candidateSet = candidates.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<IReadOnlyList<string>>();

        foreach (var rankGroup in ballot.RankGroups)
        {
            var group = rankGroup
                .Where(candidateSet.Contains)
                .Where(seen.Add)
                .ToList();
            if (group.Count > 0)
            {
                groups.Add(group);
            }
        }

        var rejected = candidates
            .Where(candidate => !seen.Contains(candidate) && ballot.Rejected.Contains(candidate))
            .ToList();
        var unranked = candidates
            .Where(candidate => !seen.Contains(candidate) && !ballot.Rejected.Contains(candidate))
            .ToList();

        if (unranked.Count > 0)
        {
            groups.Add(unranked);
        }

        if (rejected.Count > 0)
        {
            groups.Add(rejected);
        }

        return groups;
    }

    private static bool HasPath(
        IReadOnlyDictionary<string, HashSet<string>> edges,
        string start,
        string destination)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(start);

        while (pending.TryPop(out var current))
        {
            if (string.Equals(current, destination, StringComparison.Ordinal))
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var next in edges[current])
            {
                pending.Push(next);
            }
        }

        return false;
    }

    private static IReadOnlyList<string> FindSmallestCycle(
        IReadOnlyList<string> candidates,
        IReadOnlyDictionary<string, HashSet<string>> edges)
    {
        IReadOnlyList<string> smallest = [];

        foreach (var start in candidates)
        {
            foreach (var next in edges[start])
            {
                var path = FindShortestPath(edges, next, start);
                if (path.Count == 0)
                {
                    continue;
                }

                var cycle = new[] { start }.Concat(path).ToList();
                if (smallest.Count == 0 || cycle.Count < smallest.Count)
                {
                    smallest = cycle;
                }
            }
        }

        return smallest;
    }

    private static IReadOnlyList<string> FindShortestPath(
        IReadOnlyDictionary<string, HashSet<string>> edges,
        string start,
        string destination)
    {
        var pending = new Queue<IReadOnlyList<string>>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        pending.Enqueue([start]);

        while (pending.TryDequeue(out var path))
        {
            var current = path[^1];
            if (string.Equals(current, destination, StringComparison.Ordinal))
            {
                return path;
            }

            foreach (var next in edges[current])
            {
                if (visited.Add(next))
                {
                    pending.Enqueue([.. path, next]);
                }
            }
        }

        return [];
    }

    private sealed record Victory(string Winner, string Loser, int Margin, int WinningVotes);
}
