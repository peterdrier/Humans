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

        var outcome = await voteService.CastBallotAsync(voteId, userId, model.Choice, model.ToRanking(), ct);

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
    [HttpGet("{voteId:guid}/Results.csv")]
    public async Task<IActionResult> ResultsCsv(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();

        var results = await voteService.GetResultsAsync(voteId, userId, IsViewerBoardOrAdmin(), ct);
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

    private static byte[] WriteResultsCsv(AssemblyVoteResultsView results)
    {
        return HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Vote", results.Vote.Title);
            csv.WriteRow("Method", results.Result.Method);
            csv.NextRecord();

            WriteAudienceCsv(csv, "Official", results.Result.Official);
            if (results.Result.Indicative is { } indicative)
            {
                csv.NextRecord();
                WriteAudienceCsv(csv, "Indicative", indicative);
            }
        });
    }

    private static void WriteAudienceCsv(CsvWriter csv, string label, AssemblyVoteAudienceResult audience)
    {
        csv.WriteRow($"{label} roster", audience.RosterSize, "Ballots cast", audience.BallotsCast, "Verdict", audience.Verdict);

        if (audience.YesNo is { } yesNo)
        {
            csv.WriteRow("Yes", "No", "Abstain");
            csv.WriteRow(yesNo.Yes, yesNo.No, yesNo.Abstain);
            return;
        }

        if (audience.Rounds is null) return;

        var optionKeys = audience.Rounds
            .SelectMany(r => r.Counts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        csv.WriteRow(["Round", .. optionKeys, "Exhausted", "Eliminated", "Winner"]);
        foreach (var round in audience.Rounds)
        {
            var counts = optionKeys.Select(k => (object?)(round.Counts.TryGetValue(k, out var c) ? c : 0));
            csv.WriteRow([round.Number, .. counts, round.Exhausted, round.EliminatedKey, round.WinnerKey]);
        }
    }
}
