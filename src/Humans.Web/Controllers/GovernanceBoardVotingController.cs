using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Governance/BoardVoting")]
public class GovernanceBoardVotingController : HumansControllerBase
{
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly ILogger<GovernanceBoardVotingController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public GovernanceBoardVotingController(
        IUserService userService,
        IApplicationDecisionService applicationDecisionService,
        ILogger<GovernanceBoardVotingController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userService)
    {
        _applicationDecisionService = applicationDecisionService;
        _logger = logger;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> BoardVoting(CancellationToken ct)
    {
        var (applications, boardMembers) = await _applicationDecisionService.GetBoardVotingDashboardAsync(ct);

        var viewModel = new BoardVotingDashboardViewModel
        {
            BoardMembers = boardMembers
                .Select(m => new BoardVoteMemberViewModel
                {
                    UserId = m.UserId,
                    DisplayName = m.DisplayName
                })
                .ToList(),
            Applications = applications.Select(a =>
            {
                var appVm = new BoardVotingApplicationViewModel
                {
                    ApplicationId = a.ApplicationId,
                    UserId = a.UserId,
                    DisplayName = a.UserDisplayName,
                    ProfilePictureUrl = a.UserProfilePictureUrl,
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
        var application = await _applicationDecisionService.GetBoardVotingDetailAsync(applicationId, ct);
        if (application is null)
            return NotFound();

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var currentVote = application.Votes.FirstOrDefault(v => v.BoardMemberUserId == currentUser.Id);
        var isAdmin = RoleChecks.IsAdmin(User);

        var viewModel = new BoardVotingDetailViewModel
        {
            ApplicationId = application.ApplicationId,
            UserId = application.UserId,
            DisplayName = application.DisplayName,
            ProfilePictureUrl = application.ProfilePictureUrl,
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
                    DisplayName = v.BoardMemberDisplayName ?? string.Empty,
                    Vote = v.Vote,
                    Note = v.Note,
                    VotedAt = v.VotedAt.ToDateTimeUtc()
                })
                .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
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
    public async Task<IActionResult> Vote(Guid applicationId, VoteChoice vote, string? note)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await _applicationDecisionService.CastBoardVoteAsync(
                applicationId, currentUser.Id, vote, note);

            if (!result.Success)
            {
                SetError(result.ErrorKey switch
                {
                    "NotFound" => _localizer["BoardVoting_ApplicationNotFound"].Value,
                    "NotSubmitted" => _localizer["BoardVoting_ApplicationNotVotable"].Value,
                    _ => _localizer["BoardVoting_ApplicationNotVotable"].Value
                });
                return RedirectToAction(nameof(BoardVoting));
            }

            SetSuccess(_localizer["BoardVoting_VoteSaved"].Value);
            return RedirectToAction(nameof(BoardVotingDetail), new { applicationId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cast board vote for application {ApplicationId}", applicationId);
            SetError(_localizer["BoardVoting_ApplicationNotVotable"].Value);
            return RedirectToAction(nameof(BoardVoting));
        }
    }

    [HttpPost("Finalize")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Finalize(BoardVotingFinalizeModel model)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        LocalDate? meetingDate = null;
        if (!string.IsNullOrWhiteSpace(model.BoardMeetingDate))
        {
            var pattern = NodaTime.Text.LocalDatePattern.Iso;
            var parseResult = pattern.Parse(model.BoardMeetingDate);
            if (parseResult.Success)
                meetingDate = parseResult.Value;
        }

        if (meetingDate is null)
        {
            SetError(_localizer["BoardVoting_MeetingDateRequired"].Value);
            return RedirectToAction(nameof(BoardVotingDetail), new { applicationId = model.ApplicationId });
        }

        var hasVotes = await _applicationDecisionService.HasBoardVotesAsync(model.ApplicationId);
        if (!hasVotes)
        {
            SetError(_localizer["BoardVoting_NoVotes"].Value);
            return RedirectToAction(nameof(BoardVotingDetail), new { applicationId = model.ApplicationId });
        }

        try
        {
            ApplicationDecisionResult result;
            if (model.Approved)
            {
                result = await _applicationDecisionService.ApproveAsync(
                    model.ApplicationId, currentUser.Id,
                    model.DecisionNote, meetingDate);
            }
            else
            {
                result = await _applicationDecisionService.RejectAsync(
                    model.ApplicationId, currentUser.Id,
                    model.DecisionNote ?? string.Empty, meetingDate);
            }

            if (!result.Success)
            {
                _logger.LogWarning("Finalize failed for application {ApplicationId}: {ErrorKey}",
                    model.ApplicationId, result.ErrorKey);
                SetError(result.ErrorKey switch
                {
                    "NotFound" => _localizer["BoardVoting_ApplicationNotFound"].Value,
                    "NotSubmitted" => _localizer["BoardVoting_ApplicationNotVotable"].Value,
                    "ConcurrencyConflict" => _localizer["BoardVoting_ConcurrencyConflict"].Value,
                    _ => _localizer["BoardVoting_ApplicationNotVotable"].Value
                });
                return RedirectToAction(nameof(BoardVoting));
            }

            SetSuccess(_localizer["BoardVoting_Finalized"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize application {ApplicationId}", model.ApplicationId);
            SetError(_localizer["BoardVoting_ApplicationNotVotable"].Value);
        }
        return RedirectToAction(nameof(BoardVoting));
    }
}
