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
/// The embargo on tally content while a vote is Open is enforced by
/// <see cref="IAssemblyVoteService"/>, not here.
/// </summary>
[Authorize]
[Route("Governance/Votes")]
internal sealed class GovernanceVotesController(
    IUserService userService,
    IAssemblyVoteService voteService,
    IStringLocalizer<GovernanceResource> localizer,
    ILogger<GovernanceVotesController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();

        var votes = await voteService.GetVotesForMemberAsync(userId, ct);
        return View("~/Views/Governance/Votes/Index.cshtml", new AssemblyVoteListViewModel
        {
            Votes = votes.OrderByDescending(v => v.Status == AssemblyVoteStatus.Open)
                .ThenByDescending(v => v.ClosesAt).ToList()
        });
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
    /// The embargo means this never learns whether the ballot changed the tally — it reports
    /// only whether the submission was accepted.
    /// </summary>
    [HttpPost("{voteId:guid}/Ballot")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ballot(Guid voteId, AssemblyBallotFormViewModel model, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Challenge();
        if (voteId != model.VoteId) return NotFound();

        if (model.Choice == AssemblyBallotChoice.Ranked && !ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed ballot form for vote {VoteId} by {UserId}", voteId, userId);
            SetError(localizer["Governance_Votes_BallotInvalid"].Value);
            return await RedisplayBallotAsync(voteId, userId, model, ct);
        }

        // Two options at the same rank is an ambiguous ballot, and it stops here: projecting
        // to keys would hide it from the service's duplicate-key check.
        if (model.HasDuplicateRanks())
        {
            SetError(localizer["Governance_Votes_BallotDuplicateRanks"].Value);
            return await RedisplayBallotAsync(voteId, userId, model, ct);
        }

        // No choice posted at all is an empty ballot, not an affirmative one.
        if (model.Choice is not { } choice)
        {
            SetError(localizer["Governance_Votes_BallotInvalid"].Value);
            return RedirectToAction(nameof(Details), new { voteId });
        }

        var outcome = await voteService.CastBallotAsync(voteId, userId, choice, model.ToRanking(), ct);

        switch (outcome)
        {
            case BallotSubmissionOutcome.Recorded:
                SetSuccess(localizer["Governance_Votes_BallotRecorded"].Value);
                return RedirectToAction(nameof(Details), new { voteId });

            case BallotSubmissionOutcome.NotFound:
                return NotFound();

            case BallotSubmissionOutcome.NotOnRoster:
                return Forbid();

            case BallotSubmissionOutcome.VoteNotOpen:
                SetError(localizer["Governance_Votes_BallotVoteNotOpen"].Value);
                return RedirectToAction(nameof(Details), new { voteId });

            case BallotSubmissionOutcome.InvalidBallot:
            default:
                SetError(localizer["Governance_Votes_BallotInvalid"].Value);
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

    /// <summary>The same stored tally as <see cref="Results"/>, as a CSV of the YesNo counts or IRV rounds.</summary>
    /// <remarks>
    /// Asks as a plain member even for Board or Admin: requesting disclosure would load every
    /// ballot and write an <c>AssemblyBallotsViewed</c> audit entry for a download that shows
    /// nobody's ballot.
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

    /// <summary>
    /// Re-renders with the ranks actually posted rather than redirecting to a GET that rebuilds
    /// from the stored ballot, so a voter fixing one duplicated rank keeps the rest of the order.
    /// </summary>
    private async Task<IActionResult> RedisplayBallotAsync(
        Guid voteId, Guid userId, AssemblyBallotFormViewModel model, CancellationToken ct)
    {
        var vote = await voteService.GetVoteForMemberAsync(voteId, userId, ct);
        if (vote is null) return NotFound();

        if (!vote.IsOnRoster)
        {
            logger.LogWarning("Rejected ballot form outside vote {VoteId} roster for {UserId}", voteId, userId);
            return Forbid();
        }

        var rows = BuildRankedOptionRows(vote);

        // Last row wins for a key posted twice: the form never does that, and a hand-built
        // POST that does gets one of its own answers back rather than an exception.
        var posted = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var row in model.RankedOptions)
        {
            if (!string.IsNullOrWhiteSpace(row.OptionKey))
                posted[row.OptionKey] = row.Selection;
        }

        foreach (var row in rows)
        {
            if (posted.TryGetValue(row.OptionKey, out var selection)) row.Selection = selection;
        }

        return View("~/Views/Governance/Votes/Details.cshtml", new AssemblyVoteDetailViewModel
        {
            Vote = vote,
            RankedOptions = rows,
            SelectedChoice = model.Choice
        });
    }

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

    /// <summary>In the downloader's culture, with authored option labels rather than storage keys.</summary>
    private byte[] WriteResultsCsv(AssemblyVoteResultsView results)
    {
        var optionLabels = results.Vote.Options.ToDictionary(
            o => o.Key, o => o.Label, StringComparer.Ordinal);

        string OptionLabel(string? key) =>
            key is null ? string.Empty
            : optionLabels.TryGetValue(key, out var label) ? label : key;

        // Authored order, matching the results page: the authored order is the documented
        // final elimination tie-break, so the columns follow it rather than the storage keys.
        var optionKeys = results.Vote.Options.OrderBy(o => o.Order).Select(o => o.Key).ToList();

        return HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow(Text("Governance_Votes_CsvVoteLabel"), results.Vote.Title);
            csv.WriteRow(Text("Governance_Votes_MethodLabel"), Text("Governance_Votes_Method_" + results.Result.Method));
            csv.NextRecord();

            WriteAudienceCsv(csv, Text("Governance_Votes_OfficialRosterLabel"), results.Result.Official, OptionLabel, optionKeys);
            if (results.Result.Indicative is { } indicative)
            {
                csv.NextRecord();
                WriteAudienceCsv(csv, Text("Governance_Votes_IndicativeRosterLabel"), indicative, OptionLabel, optionKeys);
            }
        });
    }

    private void WriteAudienceCsv(
        CsvWriter csv,
        string rosterLabel,
        AssemblyVoteAudienceResult audience,
        Func<string?, string> optionLabel,
        IReadOnlyList<string> optionKeys)
    {
        csv.WriteRow(
            rosterLabel, audience.RosterSize,
            Text("Governance_Votes_BallotsCastLabel"), audience.BallotsCast,
            Text("Governance_Votes_VerdictLabel"), Text("Governance_Votes_Verdict_" + audience.Verdict));

        if (audience.YesNo is { } yesNo)
        {
            csv.WriteRow(
                Text("Governance_Votes_Choice_Yes"), Text("Governance_Votes_Choice_No"), Text("Governance_Votes_Choice_Abstain"));
            csv.WriteRow(yesNo.Yes, yesNo.No, yesNo.Abstain);
            return;
        }

        if (audience.Rounds is null) return;

        csv.WriteRow([
            Text("Governance_Votes_RoundColumnHeader"),
            .. optionKeys.Select(optionLabel),
            Text("Governance_Votes_ExhaustedLabel"),
            Text("Governance_Votes_EliminatedHeader"),
            Text("Governance_Votes_WinnerHeader")]);
        foreach (var round in audience.Rounds)
        {
            // Blank, not zero, for an eliminated option: zero would record nobody ranking it.
            // The results page leaves the same cell empty, and the CSV has to agree.
            var counts = optionKeys.Select(k => (object?)(round.Counts.TryGetValue(k, out var c) ? c : null));
            csv.WriteRow([
                round.Number, .. counts, round.Exhausted,
                optionLabel(round.EliminatedKey), optionLabel(round.WinnerKey)]);
        }
    }

    private string Text(string key) => localizer[key].Value;
}
