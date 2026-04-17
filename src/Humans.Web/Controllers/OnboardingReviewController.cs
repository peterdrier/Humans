using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

/// <summary>
/// Review queue for Consent Coordinators and Volunteer Coordinators.
/// Manages the consent check gate for new humans during onboarding.
/// </summary>
[Authorize(Policy = PolicyNames.ReviewQueueAccess)]
[Route("[controller]")]
public class OnboardingReviewController : HumansControllerBase
{
    private readonly IOnboardingService _onboardingService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IUserService _userService;
    private readonly ILogger<OnboardingReviewController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public OnboardingReviewController(
        UserManager<User> userManager,
        IOnboardingService onboardingService,
        IApplicationDecisionService applicationDecisionService,
        IUserService userService,
        ILogger<OnboardingReviewController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _onboardingService = onboardingService;
        _applicationDecisionService = applicationDecisionService;
        _userService = userService;
        _logger = logger;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var data = await _onboardingService.GetReviewQueueAsync(ct);

        // Batch-load users for all profiles (nav property stripped)
        var allUserIds = data.Pending.Concat(data.Flagged).Select(p => p.UserId).Distinct().ToList();
        var usersById = await _userService.GetByIdsAsync(allUserIds, ct);

        var viewModel = new OnboardingReviewIndexViewModel
        {
            PendingReviews = data.Pending.Select(p => MapToItem(p, usersById, data.PendingAppUserIds, data.ConsentProgress)).ToList(),
            FlaggedReviews = data.Flagged.Select(p => MapToItem(p, usersById, data.PendingAppUserIds, data.ConsentProgress)).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Detail(Guid userId, CancellationToken ct)
    {
        var detail = await _onboardingService.GetReviewDetailAsync(userId, ct);
        var (profile, consentCount, requiredConsentCount, pendingApp) =
            (detail.Profile, detail.ConsentCount, detail.RequiredConsentCount, detail.PendingApplication);

        if (profile is null)
            return NotFound();

        var detailUser = await _userService.GetByIdAsync(userId, ct);

        var viewModel = new OnboardingReviewDetailViewModel
        {
            UserId = userId,
            DisplayName = detailUser?.DisplayName ?? "Unknown",
            ProfilePictureUrl = detailUser?.ProfilePictureUrl,
            Email = detailUser?.Email ?? string.Empty,
            FirstName = profile.FirstName,
            LastName = profile.LastName,
            City = profile.City,
            CountryCode = profile.CountryCode,
            MembershipTier = profile.MembershipTier,
            ConsentCheckStatus = profile.ConsentCheckStatus,
            ConsentCheckNotes = profile.ConsentCheckNotes,
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            ConsentCount = consentCount,
            RequiredConsentCount = requiredConsentCount,
            HasPendingApplication = pendingApp is not null,
            ApplicationMotivation = pendingApp?.Motivation
        };

        return View(viewModel);
    }

    [HttpPost("{userId:guid}/Clear")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> Clear(Guid userId, string? notes)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await _onboardingService.ClearConsentCheckAsync(
                userId, currentUser.Id, notes);

            if (!result.Success)
            {
                SetError(result.ErrorKey switch
                {
                    "AlreadyRejected" => _localizer["OnboardingReview_AlreadyRejected"].Value,
                    _ => _localizer["Common_Error"].Value
                });
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(_localizer["OnboardingReview_Cleared"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear consent check for user {UserId}", userId);
            SetError(_localizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/Flag")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> Flag(Guid userId, string? notes)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await _onboardingService.FlagConsentCheckAsync(
                userId, currentUser.Id, notes);

            if (!result.Success)
            {
                SetError(_localizer["Common_Error"].Value);
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(_localizer["OnboardingReview_Flagged"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flag consent check for user {UserId}", userId);
            SetError(_localizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/Reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> Reject(Guid userId, string? reason)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await _onboardingService.RejectSignupAsync(
                userId, currentUser.Id, reason);

            if (!result.Success)
            {
                SetError(string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal)
                    ? _localizer["OnboardingReview_AlreadyRejected"].Value
                    : _localizer["Common_Error"].Value);
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(_localizer["OnboardingReview_Rejected"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject signup for user {UserId}", userId);
            SetError(_localizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("BoardVoting")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> BoardVoting(CancellationToken ct)
    {
        var (applications, boardMembers) = await _onboardingService.GetBoardVotingDashboardAsync(ct);

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

        return View(viewModel);
    }

    [HttpGet("BoardVoting/{applicationId:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> BoardVotingDetail(Guid applicationId, CancellationToken ct)
    {
        var application = await _onboardingService.GetBoardVotingDetailAsync(applicationId, ct);
        if (application is null)
            return NotFound();

        var currentUser = await GetCurrentUserAsync();
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

        return View(viewModel);
    }

    [HttpPost("BoardVoting/Vote")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.BoardOnly)]
    public async Task<IActionResult> Vote(Guid applicationId, VoteChoice vote, string? note)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await _onboardingService.CastBoardVoteAsync(
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

    [HttpPost("BoardVoting/Finalize")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Finalize(BoardVotingFinalizeModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        // Require a valid meeting date (validated in controller as it's form input)
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

        // Require at least one board vote before finalization
        var hasVotes = await _onboardingService.HasBoardVotesAsync(model.ApplicationId);
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

    private static OnboardingReviewItemViewModel MapToItem(
        Profile profile, IReadOnlyDictionary<Guid, User> usersById,
        HashSet<Guid> pendingAppUserIds,
        Dictionary<Guid, ConsentProgressInfo> consentProgress)
    {
        var progress = consentProgress.GetValueOrDefault(profile.UserId);
        var itemUser = usersById.GetValueOrDefault(profile.UserId);
        return new OnboardingReviewItemViewModel
        {
            UserId = profile.UserId,
            DisplayName = itemUser?.DisplayName ?? "Unknown",
            ProfilePictureUrl = itemUser?.ProfilePictureUrl,
            Email = itemUser?.Email ?? string.Empty,
            ConsentCheckStatus = profile.ConsentCheckStatus,
            MembershipTier = profile.MembershipTier,
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            HasPendingApplication = pendingAppUserIds.Contains(profile.UserId),
            ConsentCount = progress?.Signed ?? 0,
            RequiredConsentCount = progress?.Required ?? 0
        };
    }
}
