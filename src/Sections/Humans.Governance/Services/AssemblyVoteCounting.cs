using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;

namespace Humans.Governance.Services;

/// <summary>
/// The counting rules for assembly votes: YesNo majorities and instant-runoff rounds.
/// </summary>
/// <remarks>
/// Deliberately pure — no repository, no clock, no I/O — so the association's counting
/// rules can be tested directly and read by a member who wants to check a result by hand.
/// The service calls this exactly twice: once at close, to compute the result it stores,
/// and once behind the audited Admin peek.
/// <para>
/// Nothing here ever resolves a tie. Statutes Art. 10.2 gives the President of the Board
/// the casting vote on a tie between opposing options, and the Secretary records it in the
/// acta; a tie therefore leaves this code as <see cref="AssemblyVoteVerdict.Tie"/>.
/// </para>
/// </remarks>
internal static class AssemblyVoteCounting
{
    /// <summary>
    /// One ballot reduced to what counting needs. Keeps the counting rules independent of
    /// the EF entity, which never leaves the section.
    /// </summary>
    /// <param name="Choice">The recorded answer.</param>
    /// <param name="Ranking">Preference order, best first; RankedChoice only.</param>
    internal sealed record CountableBallot(AssemblyBallotChoice Choice, IReadOnlyList<string>? Ranking);

    /// <summary>
    /// Counts a YesNo vote. Abstentions are tallied and shown but excluded from the
    /// majority on both sides (statutes Art. 10.2).
    /// </summary>
    public static (YesNoTally Tally, AssemblyVoteVerdict Verdict) CountYesNo(
        IReadOnlyCollection<CountableBallot> ballots,
        RequiredMajority requiredMajority)
    {
        var yes = ballots.Count(b => b.Choice == AssemblyBallotChoice.Yes);
        var no = ballots.Count(b => b.Choice == AssemblyBallotChoice.No);
        var abstain = ballots.Count(b => b.Choice == AssemblyBallotChoice.Abstain);

        return (new YesNoTally(yes, no, abstain), Verdict(yes, no, requiredMajority));
    }

    private static AssemblyVoteVerdict Verdict(int yes, int no, RequiredMajority requiredMajority)
    {
        // Nobody voted for or against: there is no majority to have reached, whatever the
        // threshold arithmetic would say about zero.
        if (yes + no == 0)
        {
            return requiredMajority == RequiredMajority.Simple
                ? AssemblyVoteVerdict.Tie
                : AssemblyVoteVerdict.Failed;
        }

        if (requiredMajority == RequiredMajority.TwoThirds)
        {
            // ceil(2/3 × cast) without floating point, so the boundary case is exact.
            var threshold = (2 * (yes + no) + 2) / 3;
            return yes >= threshold ? AssemblyVoteVerdict.Passed : AssemblyVoteVerdict.Failed;
        }

        if (yes > no) return AssemblyVoteVerdict.Passed;
        return yes < no ? AssemblyVoteVerdict.Failed : AssemblyVoteVerdict.Tie;
    }

    /// <summary>
    /// Counts a RankedChoice vote by instant runoff, returning every round so the page can
    /// show the eliminations.
    /// </summary>
    /// <param name="ballots">The ballots to count; abstentions are exhausted from the start.</param>
    /// <param name="options">
    /// The authored options, whose <see cref="AssemblyVoteOption.Order"/> is the final,
    /// disclosed elimination tie-break.
    /// </param>
    /// <remarks>
    /// An option wins when it holds more than half of the continuing (non-exhausted)
    /// ballots in a round. Otherwise the fewest-votes option is eliminated, breaking a tie
    /// for elimination by fewest votes in the previous round and then by authored order.
    /// A deciding round that is level is reported as a tie with no winner rather than
    /// broken by authored order — with ~120 Asociados that is unlikely, and inventing a
    /// winner is exactly what Art. 10.2 reserves for the President.
    /// </remarks>
    public static (IReadOnlyList<InstantRunoffRound> Rounds, string? WinnerKey,
        AssemblyVoteVerdict Verdict, IReadOnlyList<AssemblyVoteNote> Notes) CountInstantRunoff(
        IReadOnlyCollection<CountableBallot> ballots,
        IReadOnlyList<AssemblyVoteOption> options)
    {
        var order = options.OrderBy(o => o.Order)
            .Select((o, i) => (o.Key, Index: i))
            .ToDictionary(x => x.Key, x => x.Index, StringComparer.Ordinal);

        var continuing = options.OrderBy(o => o.Order)
            .Select(o => o.Key)
            .ToList();

        // Abstentions and ballots with no usable ranking never enter the runoff.
        var rankings = ballots
            .Where(b => b.Choice == AssemblyBallotChoice.Ranked && b.Ranking is { Count: > 0 })
            .Select(b => b.Ranking!)
            .ToList();

        var rounds = new List<InstantRunoffRound>();
        var notes = new List<AssemblyVoteNote>();
        var previousCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalBallots = ballots.Count;

        while (true)
        {
            var counts = continuing.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
            var counted = 0;

            foreach (var ranking in rankings)
            {
                // A ballot's vote in this round is its highest-ranked option still standing.
                // A partial ranking is valid; it simply exhausts once its options are gone.
                var preference = ranking.FirstOrDefault(counts.ContainsKey);
                if (preference is null) continue;
                counts[preference]++;
                counted++;
            }

            var exhausted = totalBallots - counted;
            var roundNumber = rounds.Count + 1;

            // Majority of the continuing ballots, not of the roster: exhausted ballots
            // leave the denominator, which is what makes a runoff terminate.
            var winner = counted > 0
                ? counts.FirstOrDefault(kv => kv.Value * 2 > counted).Key
                : null;

            if (winner is not null)
            {
                rounds.Add(new InstantRunoffRound(roundNumber, counts, exhausted, null, winner));
                return (rounds, winner, AssemblyVoteVerdict.Passed, notes);
            }

            if (continuing.Count <= 1)
            {
                // One option left and it still holds no majority: every ballot that ranked
                // it is gone, so there is nothing to declare.
                rounds.Add(new InstantRunoffRound(roundNumber, counts, exhausted, null, null));
                notes.Add(new AssemblyVoteNote(AssemblyVoteNoteKind.NoMajority, 0, 0));
                return (rounds, null, AssemblyVoteVerdict.Tie, notes);
            }

            var lowest = counts.Values.Min();
            var tiedForElimination = continuing.Where(k => counts[k] == lowest).ToList();

            // A level deciding round is a tie, not an elimination: with two options left
            // and equal counts, eliminating either one manufactures a winner.
            if (continuing.Count == 2 && tiedForElimination.Count == 2)
            {
                rounds.Add(new InstantRunoffRound(roundNumber, counts, exhausted, null, null));
                notes.Add(new AssemblyVoteNote(AssemblyVoteNoteKind.DecidingRoundLevel, roundNumber, 2));
                return (rounds, null, AssemblyVoteVerdict.Tie, notes);
            }

            var eliminated = Eliminate(tiedForElimination, previousCounts, order, roundNumber, notes);

            rounds.Add(new InstantRunoffRound(roundNumber, counts, exhausted, eliminated, null));
            continuing.Remove(eliminated);
            previousCounts = counts;
        }
    }

    /// <summary>
    /// Picks which of the options tied for last place is eliminated: fewest votes in the
    /// previous round first, then authored order. Every step actually used is recorded in
    /// <paramref name="notes"/>, because the page discloses the method.
    /// </summary>
    private static string Eliminate(
        List<string> tied,
        Dictionary<string, int> previousCounts,
        Dictionary<string, int> authoredOrder,
        int roundNumber,
        List<AssemblyVoteNote> notes)
    {
        if (tied.Count == 1) return tied[0];

        if (previousCounts.Count > 0)
        {
            var fewestBefore = tied.Min(k => previousCounts.GetValueOrDefault(k));
            var stillTied = tied.Where(k => previousCounts.GetValueOrDefault(k) == fewestBefore).ToList();
            if (stillTied.Count == 1)
            {
                notes.Add(new AssemblyVoteNote(
                    AssemblyVoteNoteKind.TieBrokenByPreviousRound, roundNumber, tied.Count));
                return stillTied[0];
            }

            tied = stillTied;
        }

        notes.Add(new AssemblyVoteNote(
            AssemblyVoteNoteKind.TieBrokenByAuthoredOrder, roundNumber, tied.Count));
        return tied.OrderBy(k => authoredOrder.GetValueOrDefault(k, int.MaxValue), Comparer<int>.Default)
            .First();
    }
}
