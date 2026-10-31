using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Governance.Services;
using Humans.Governance.Domain;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Base.Authorization;
using Humans.Governance.Models;
using Humans.Users.Contracts;

namespace Humans.Governance.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Governance/BoardVoting")]
internal sealed class GovernanceBoardVotingController(
    IUserService userService,
    ApplicationDecisionService applicationDecisionService,
    ILogger<GovernanceBoardVotingController> logger,
    IStringLocalizer<GovernanceResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> BoardVoting(CancellationToken ct)
    {
        var (applications, boardMembers) = await applicationDecisionService.GetBoardVotingDashboardAsync(ct);

        var viewModel = new BoardVotingDashboardViewModel
        {
            BoardMembers = boardMembers
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(m => new BoardVoteMemberViewModel
                {
                    UserId = m.UserId
                })
                .ToList(),
            Applications = applications
                .OrderBy(a => a.MembershipTier)
                .ThenBy(a => a.SubmittedAt)
                .Select(a =>
            {
                var appVm = new BoardVotingApplicationViewModel
                {
                    ApplicationId = a.ApplicationId,
                    UserId = a.UserId,
                    MembershipTier = a.MembershipTier,
                    ApplicationMotivation = a.ApplicationMotivation,
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc(),
                    Status = a.Status
                };
                foreach (var vote in a.Votes)
                {
                    appVm.VotesByBoardMember[vote.BoardMemberUserId] = new BoardVoteCellViewModel
                    {
                        Vote = vote.Vote,
                        Note = vote.Note
                    };
                }
                return appVm;
            }).ToList(),
        };

        return View("~/Views/Governance/BoardVoting/Index.cshtml", viewModel);
    }

    [HttpGet("{applicationId:guid}")]
    public async Task<IActionResult> BoardVotingDetail(Guid applicationId, CancellationToken ct)
    {
        var application = await applicationDecisionService.GetBoardVotingDetailAsync(applicationId, ct);
        if (application is null)
            return NotFound();

        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();

        var currentVote = application.Votes.FirstOrDefault(v => v.BoardMemberUserId == currentUser.Id);
        var isAdmin = RoleChecks.IsAdmin(User);

        var viewModel = new BoardVotingDetailViewModel
        {
            ApplicationId = application.ApplicationId,
            UserId = application.UserId,
            Email = application.Email,
            FirstName = application.FirstName,
            LastName = application.LastName,
            City = application.City,
            CountryCode = application.CountryCode,
            MembershipTier = application.MembershipTier,
            Status = application.Status,
            ApplicationMotivation = application.Motivation,
            AdditionalInfo = application.AdditionalInfo,
            SignificantContribution = application.SignificantContribution,
            RoleUnderstanding = application.RoleUnderstanding,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            Votes = application.Votes
                .Select(v => new BoardVoteDetailItemViewModel
                {
                    BoardMemberUserId = v.BoardMemberUserId,
                    Vote = v.Vote,
                    Note = v.Note,
                    VotedAt = v.VotedAt.ToDateTimeUtc()
                })
                .OrderBy(v => v.VotedAt)
                .ToList(),
            CurrentUserVote = currentVote?.Vote,
            CurrentUserNote = currentVote?.Note,
            CanFinalize = isAdmin
        };

        return View("~/Views/Governance/BoardVoting/Detail.cshtml", viewModel);
    }

    [HttpPost("Vote")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.BoardOnly)]
    public async Task<IActionResult> Vote(Guid applicationId, [BindRequired] VoteChoice vote, string? note)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid board decision form for application {ApplicationId}", applicationId);
            return BadRequest(ModelState);
        }

        try
        {
            var result = await applicationDecisionService.CastBoardVoteAsync(
                applicationId, currentUser.Id, vote, note);

            if (!result.Success)
            {
                SetError(result.ErrorKey switch
                {
                    "NotFound" => localizer["Governance_BoardVoting_ApplicationNotFound"].Value,
                    "NoteTooLong" => localizer["Governance_BoardVoting_NoteTooLong"].Value,
                    _ => localizer["Governance_BoardVoting_ApplicationNotVotable"].Value
                });
                return result.ErrorKey is "NoteTooLong"
                    ? RedirectToAction(nameof(BoardVotingDetail), new { applicationId })
                    : RedirectToAction(nameof(BoardVoting));
            }

            SetSuccess(localizer["Governance_BoardVoting_VoteSaved"].Value);
            return RedirectToAction(nameof(BoardVotingDetail), new { applicationId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cast board vote for application {ApplicationId}", applicationId);
            SetError(localizer["Governance_BoardVoting_ApplicationNotVotable"].Value);
            return RedirectToAction(nameof(BoardVoting));
        }
    }

    [HttpPost("Finalize")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Finalize(BoardVotingFinalizeModel model)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid board decision form for application {ApplicationId}", model.ApplicationId);
            return BadRequest(ModelState);
        }

        LocalDate? meetingDate = null;
        if (!string.IsNullOrWhiteSpace(model.BoardMeetingDate))
        {
            var parseResult = NodaTime.Text.LocalDatePattern.Iso.Parse(model.BoardMeetingDate);
            if (parseResult.Success)
                meetingDate = parseResult.Value;
        }

        if (meetingDate is null)
        {
            SetError(localizer["Governance_BoardVoting_MeetingDateRequired"].Value);
            return RedirectToAction(nameof(BoardVotingDetail), new { applicationId = model.ApplicationId });
        }

        try
        {
            var result = model.Approved
                ? await applicationDecisionService.ApproveAsync(
                    model.ApplicationId, currentUser.Id,
                    model.DecisionNote, meetingDate)
                : await applicationDecisionService.RejectAsync(
                    model.ApplicationId, currentUser.Id,
                    model.DecisionNote ?? string.Empty, meetingDate);

            if (!result.Success)
            {
                logger.LogWarning("Finalize failed for application {ApplicationId}: {ErrorKey}",
                    model.ApplicationId, result.ErrorKey);
                var (messageKey, returnToDetail) = FinalizeErrorMessage(result.ErrorKey);
                SetError(localizer[messageKey].Value);
                return returnToDetail
                    ? RedirectToAction(nameof(BoardVotingDetail), new { applicationId = model.ApplicationId })
                    : RedirectToAction(nameof(BoardVoting));
            }

            SetSuccess(localizer["Governance_BoardVoting_Finalized"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize application {ApplicationId}", model.ApplicationId);
            SetError(localizer["Governance_BoardVoting_ApplicationNotVotable"].Value);
        }
        return RedirectToAction(nameof(BoardVoting));
    }

    private static (string MessageKey, bool ReturnToDetail) FinalizeErrorMessage(string? errorKey) => errorKey switch
    {
        "NotFound" => ("Governance_BoardVoting_ApplicationNotFound", false),
        "NoVotes" => ("Governance_BoardVoting_NoVotes", true),
        "DecisionNoteTooLong" => ("Governance_BoardVoting_DecisionNoteTooLong", true),
        "DecisionNoteRequired" => ("Governance_BoardVoting_DecisionNoteRequired", true),
        _ => ("Governance_BoardVoting_ApplicationNotVotable", false)
    };
}
