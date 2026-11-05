using System.Globalization;
using System.Text;
using Humans.Base.Extensions;
using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Services;

internal static class AssemblyVotePresentation
{
    /// <summary>
    /// The acta block: a plain-text summary the Secretary pastes into the minutes. Deliberately
    /// unlocalized and unstyled — it goes into a Spanish legal document, and the numbers are
    /// what statutes Art. 8.6 requires.
    /// </summary>
    internal static string BuildActa(
        AssemblyVote vote, AssemblyVoteResult result, string? closedByName)
    {
        var acta = new StringBuilder();
        var official = result.Official;

        // The counting result is keyed by option key; the acta is read by people, in the vote's
        // official culture like the title above it.
        string Label(string key) =>
            vote.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal))
                is { } option
                ? option.Label.Resolve(vote.OfficialCulture, vote.OfficialCulture)
                : key;

        acta.AppendLine(CultureInfo.InvariantCulture,
            $"Assembly vote: {vote.Title.Resolve(vote.OfficialCulture, vote.OfficialCulture)}");
        if (vote.AssemblyDate is { } date)
        {
            acta.AppendLine(CultureInfo.InvariantCulture, $"Assembly date: {date.ToInvariantDate()}");
        }

        acta.AppendLine(CultureInfo.InvariantCulture,
            $"Method: {result.Method}, majority required: {vote.RequiredMajority}");
        acta.AppendLine(CultureInfo.InvariantCulture,
            $"Electorate: {official.RosterSize}; ballots cast: {official.BallotsCast}");

        if (official.YesNo is { } yesNo)
        {
            acta.AppendLine(CultureInfo.InvariantCulture,
                $"In favour: {yesNo.Yes}; against: {yesNo.No}; abstentions: {yesNo.Abstain}");
        }
        else if (official.Rounds is { Count: > 0 } rounds)
        {
            foreach (var round in rounds)
            {
                var counts = string.Join(", ", round.Counts.Select(kv => $"{Label(kv.Key)}: {kv.Value}"));
                var eliminated = round.EliminatedKey is null
                    ? string.Empty
                    : $"; eliminated: {Label(round.EliminatedKey)}";
                acta.AppendLine(CultureInfo.InvariantCulture,
                    $"Round {round.Number}: {counts}; exhausted: {round.Exhausted}{eliminated}");
            }

            if (official.WinnerKey is { } winnerKey)
            {
                acta.AppendLine(CultureInfo.InvariantCulture, $"Winning option: {Label(winnerKey)}");
            }
        }

        acta.AppendLine(CultureInfo.InvariantCulture, $"Result: {official.Verdict}");
        if (official.Verdict == AssemblyVoteVerdict.Tie)
        {
            acta.AppendLine("Tied. Under statutes Art. 10.2 the President of the Board casts the "
                            + "deciding vote; record it here.");
        }

        // In the association's own timezone, like every closing time the electorate was
        // shown. A UTC instant here reads two hours early in summer, and the acta is the
        // document the Secretary files.
        acta.AppendLine(vote.ClosedAt is { } closedAt
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Closed at: {ClosingLocal(closedAt)} (Europe/Madrid)")
            : "Closed at: not recorded.");
        // The spec lists "who closed it" among the acta's contents, and this is the summary
        // the Secretary files: "an administrator" names nobody.
        acta.AppendLine(vote.ClosedByUserId is null
            ? "Closed automatically at the announced time."
            : closedByName is null
                ? "Closed by an administrator."
                : $"Closed by {closedByName}.");

        // No indicative line: the acta is the association's legal record of the binding vote,
        // and the spec keeps indicative ballots out of it. The results page shows them in
        // their own block, which is where an advisory number belongs.
        return acta.ToString();
    }

    /// <summary>
    /// The closing time in the association's own timezone, still unformatted: Email renders it
    /// under each recipient's culture, so the zone is decided here and the wording there.
    /// </summary>
    internal static LocalDateTime ClosingLocal(Instant closesAt) =>
        closesAt.InZone(MadridZone).LocalDateTime;

    /// <summary>
    /// Closing times are announced to the electorate in the association's own timezone; a
    /// UTC instant in an email would be a different deadline to every reader.
    /// </summary>
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

}
