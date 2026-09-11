using CsvHelper;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Base.Csv;
using Humans.Governance.Domain;
using Humans.Governance.Models;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Governance.Controllers;

/// <summary>
/// Member-facing assembly-vote pages: list, ballot page, cast/change, and closed results. Every
/// logged-in member sees every vote; only roster members can cast (Docs/features/assembly-votes.md
/// "Actors &amp; roles"). The embargo on tally content while a vote is Open is enforced by
/// <see cref="IAssemblyVoteService"/>, not here — this controller never reaches for a shortcut
/// around it.
/// </summary>
[Authorize]
[Route("Governance/Votes")]
internal sealed class GovernanceVotesController(
    IUserServiceRead userService,
    IAssemblyVoteService voteService,
    IStringLocalizer<GovernanceResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();

        var votes = await voteService.GetVotesForMemberAsync(userId, ct);
        return View("~/Views/Governance/Votes/Index.cshtml", new AssemblyVoteListViewModel { Votes = votes });
    }

    [HttpGet("{voteId:guid}")]
    public async Task<IActionResult> Details(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();

        var vote = await voteService.GetVoteForMemberAsync(voteId, userId, ct);
        if (vote is null) return NotFound();

        var rankedOptions = vote.Kind == AssemblyVoteKind.RankedChoice
            ? BuildRankedOptionRows(vote)
            : [];

        return View("~/Views/Governance/Votes/Details.cshtml", new AssemblyVoteDetailViewModel
        {
            Vote = vote,
            RankedOptions = rankedOptions
        });
    }

    /// <summary>
    /// Casts or changes the caller's ballot. The embargo means this action never learns whether
    /// the ballot it just recorded changed the tally — it only reports whether the submission
    /// itself was accepted.
    /// </summary>
    [HttpPost("{voteId:guid}/Ballot")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ballot(Guid voteId, AssemblyBallotFormViewModel model, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();
        if (voteId != model.VoteId) return NotFound();

        // Two options at the same rank is an ambiguous ballot, and it stops here: projecting
        // to keys would hide it from the service's duplicate-key check.
        if (model.HasDuplicateRanks())
        {
            SetError(localizer["Votes_BallotDuplicateRanks"].Value);
            return RedirectToAction(nameof(Details), new { voteId });
        }

        // No choice posted at all is an empty ballot, not an affirmative one.
        if (model.Choice is not { } choice)
        {
            SetError(localizer["Votes_BallotInvalid"].Value);
            return RedirectToAction(nameof(Details), new { voteId });
        }

        var outcome = await voteService.CastBallotAsync(voteId, userId, choice, model.ToRanking(), ct);

        switch (outcome)
        {
            case BallotSubmissionOutcome.Recorded:
                SetSuccess(localizer["Votes_BallotRecorded"].Value);
                return RedirectToAction(nameof(Details), new { voteId });

            case BallotSubmissionOutcome.NotFound:
                return NotFound();

            case BallotSubmissionOutcome.NotOnRoster:
                return Forbid();

            case BallotSubmissionOutcome.VoteNotOpen:
                SetError(localizer["Votes_BallotVoteNotOpen"].Value);
                return RedirectToAction(nameof(Details), new { voteId });

            case BallotSubmissionOutcome.InvalidBallot:
            default:
                SetError(localizer["Votes_BallotInvalid"].Value);
                return RedirectToAction(nameof(Details), new { voteId });
        }
    }

    [HttpGet("{voteId:guid}/Results")]
    public async Task<IActionResult> Results(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();

        var results = await voteService.GetResultsAsync(voteId, userId, IsViewerBoardOrAdmin(), ct);
        if (results is null) return NotFound();

        return View("~/Views/Governance/Votes/Results.cshtml", new AssemblyVoteResultsViewModel { Results = results });
    }

    /// <summary>The same stored tally as <see cref="Results"/>, as a CSV of the YesNo counts or IRV rounds — the export named in US-V7.</summary>
    /// <remarks>
    /// Asks for the results as a plain member even when the caller is Board or Admin: the CSV
    /// writes tallies and rounds only, so requesting disclosure would load every individual
    /// ballot and write an <c>AssemblyBallotsViewed</c> audit entry for a download that shows
    /// nobody's ballot. The numbers below are identical either way.
    /// </remarks>
    [HttpGet("{voteId:guid}/Results.csv")]
    public async Task<IActionResult> ResultsCsv(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();

        var results = await voteService.GetResultsAsync(voteId, userId, viewerIsBoardOrAdmin: false, ct);
        if (results is null) return NotFound();

        var bytes = WriteResultsCsv(results);
        return File(bytes, "text/csv; charset=utf-8", $"vote-{voteId}-results.csv");
    }

    private bool IsViewerBoardOrAdmin() => RoleChecks.IsAdminOrBoard(User);

    private static IReadOnlyList<AssemblyRankedBallotOptionRow> BuildRankedOptionRows(AssemblyVoteDetail vote)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (vote.OwnBallot?.Ranking is { } ranking)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                ranks[ranking[i]] = i + 1;
            }
        }

        return vote.Options
            .OrderBy(o => o.Order)
            .Select(o => new AssemblyRankedBallotOptionRow
            {
                OptionKey = o.Key,
                Label = o.Label,
                Selection = ranks.TryGetValue(o.Key, out var rank) ? rank : null
            })
            .ToList();
    }

    /// <summary>
    /// The results CSV, in the downloader's culture. It is a member-facing export of the same
    /// numbers the results page shows, so every label goes through the section's resources and
    /// option columns carry the authored labels rather than the storage keys.
    /// </summary>
    private byte[] WriteResultsCsv(AssemblyVoteResultsView results)
    {
        var optionLabels = results.Vote.Options.ToDictionary(
            o => o.Key, o => o.Label, StringComparer.Ordinal);

        string OptionLabel(string? key) =>
            key is null ? string.Empty
            : optionLabels.TryGetValue(key, out var label) ? label : key;

        return HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow(Text("Votes_CsvVoteLabel"), results.Vote.Title);
            csv.WriteRow(Text("Votes_MethodLabel"), Text("Votes_Method_" + results.Result.Method));
            csv.NextRecord();

            WriteAudienceCsv(csv, Text("Votes_OfficialRosterLabel"), results.Result.Official, OptionLabel);
            if (results.Result.Indicative is { } indicative)
            {
                csv.NextRecord();
                WriteAudienceCsv(csv, Text("Votes_IndicativeRosterLabel"), indicative, OptionLabel);
            }
        });
    }

    private void WriteAudienceCsv(
        CsvWriter csv,
        string rosterLabel,
        AssemblyVoteAudienceResult audience,
        Func<string?, string> optionLabel)
    {
        csv.WriteRow(
            rosterLabel, audience.RosterSize,
            Text("Votes_BallotsCastLabel"), audience.BallotsCast,
            Text("Votes_VerdictLabel"), Text("Votes_Verdict_" + audience.Verdict));

        if (audience.YesNo is { } yesNo)
        {
            csv.WriteRow(
                Text("Votes_Choice_Yes"), Text("Votes_Choice_No"), Text("Votes_Choice_Abstain"));
            csv.WriteRow(yesNo.Yes, yesNo.No, yesNo.Abstain);
            return;
        }

        if (audience.Rounds is null) return;

        var optionKeys = audience.Rounds
            .SelectMany(r => r.Counts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        csv.WriteRow([
            Text("Votes_RoundColumnHeader"),
            .. optionKeys.Select(optionLabel),
            Text("Votes_ExhaustedLabel"),
            Text("Votes_EliminatedHeader"),
            Text("Votes_WinnerHeader")]);
        foreach (var round in audience.Rounds)
        {
            var counts = optionKeys.Select(k => (object?)(round.Counts.TryGetValue(k, out var c) ? c : 0));
            csv.WriteRow([
                round.Number, .. counts, round.Exhausted,
                optionLabel(round.EliminatedKey), optionLabel(round.WinnerKey)]);
        }
    }

    private string Text(string key) => localizer[key].Value;
}
