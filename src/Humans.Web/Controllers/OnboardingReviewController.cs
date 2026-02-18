using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

/// <summary>
/// Review queue for Consent Coordinators and Volunteer Coordinators.
/// Manages the consent check gate for new humans during onboarding.
/// </summary>
[Authorize(Roles = $"{RoleNames.ConsentCoordinator},{RoleNames.VolunteerCoordinator},{RoleNames.Board},{RoleNames.Admin}")]
[Route("[controller]")]
public class OnboardingReviewController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly SystemTeamSyncJob _syncJob;
    private readonly ILogger<OnboardingReviewController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public OnboardingReviewController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        IMembershipCalculator membershipCalculator,
        IAuditLogService auditLogService,
        IEmailService emailService,
        SystemTeamSyncJob syncJob,
        ILogger<OnboardingReviewController> logger,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _clock = clock;
        _membershipCalculator = membershipCalculator;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _syncJob = syncJob;
        _logger = logger;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var reviewableProfiles = await _dbContext.Profiles
            .Include(p => p.User)
            .Where(p => p.ConsentCheckStatus == ConsentCheckStatus.Pending ||
                        p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        var allUserIds = reviewableProfiles.Select(p => p.UserId).ToList();
        var pendingAppUserIds = await _dbContext.Applications
            .Where(a => allUserIds.Contains(a.UserId) &&
                (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview))
            .Select(a => a.UserId)
            .ToHashSetAsync();

        var grouped = reviewableProfiles
            .ToLookup(p => p.ConsentCheckStatus);

        var viewModel = new OnboardingReviewIndexViewModel
        {
            PendingReviews = grouped[ConsentCheckStatus.Pending].Select(p => MapToItem(p, pendingAppUserIds)).ToList(),
            FlaggedReviews = grouped[ConsentCheckStatus.Flagged].Select(p => MapToItem(p, pendingAppUserIds)).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Detail(Guid userId)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            return NotFound();
        }

        var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId);

        var pendingApp = await _dbContext.Applications
            .Where(a => a.UserId == userId &&
                (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview))
            .FirstOrDefaultAsync();

        var viewModel = new OnboardingReviewDetailViewModel
        {
            UserId = userId,
            DisplayName = profile.User.DisplayName,
            ProfilePictureUrl = profile.User.ProfilePictureUrl,
            Email = profile.User.Email ?? string.Empty,
            FirstName = profile.FirstName,
            LastName = profile.LastName,
            City = profile.City,
            CountryCode = profile.CountryCode,
            MembershipTier = profile.MembershipTier,
            ConsentCheckStatus = profile.ConsentCheckStatus,
            ConsentCheckNotes = profile.ConsentCheckNotes,
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            ConsentCount = snapshot.RequiredConsentCount - snapshot.PendingConsentCount,
            RequiredConsentCount = snapshot.RequiredConsentCount,
            HasPendingApplication = pendingApp != null,
            ApplicationMotivation = pendingApp?.Motivation
        };

        return View(viewModel);
    }

    [HttpPost("{userId:guid}/Clear")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = $"{RoleNames.ConsentCoordinator},{RoleNames.Board},{RoleNames.Admin}")]
    public async Task<IActionResult> Clear(Guid userId, string? notes)
    {
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return NotFound();
        }

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Cleared;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = currentUser.Id;
        profile.ConsentCheckNotes = notes;

        // Auto-approve as Volunteer
        profile.IsApproved = true;
        profile.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();

        // Sync Volunteers team membership (adds to team + sends welcome email)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId, CancellationToken.None);

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckCleared, "Profile", userId,
            $"Consent check cleared by {currentUser.DisplayName}",
            currentUser.Id, currentUser.DisplayName);

        _logger.LogInformation("Consent check cleared for user {UserId} by {ReviewerId}", userId, currentUser.Id);

        TempData["SuccessMessage"] = _localizer["OnboardingReview_Cleared"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/Flag")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = $"{RoleNames.ConsentCoordinator},{RoleNames.Board},{RoleNames.Admin}")]
    public async Task<IActionResult> Flag(Guid userId, string? notes)
    {
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return NotFound();
        }

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Flagged;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = currentUser.Id;
        profile.ConsentCheckNotes = notes;
        profile.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckFlagged, "Profile", userId,
            $"Consent check flagged by {currentUser.DisplayName}: {notes}",
            currentUser.Id, currentUser.DisplayName);

        _logger.LogInformation("Consent check flagged for user {UserId} by {ReviewerId}", userId, currentUser.Id);

        TempData["SuccessMessage"] = _localizer["OnboardingReview_Flagged"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/Reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = $"{RoleNames.ConsentCoordinator},{RoleNames.Board},{RoleNames.Admin}")]
    public async Task<IActionResult> Reject(Guid userId, string? reason)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile == null)
        {
            return NotFound();
        }

        if (profile.RejectedAt != null)
        {
            TempData["ErrorMessage"] = _localizer["OnboardingReview_AlreadyRejected"].Value;
            return RedirectToAction(nameof(Index));
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return NotFound();
        }

        var now = _clock.GetCurrentInstant();

        profile.RejectionReason = reason;
        profile.RejectedAt = now;
        profile.RejectedByUserId = currentUser.Id;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();

        await _auditLogService.LogAsync(
            AuditAction.SignupRejected, "Profile", userId,
            $"Signup rejected by {currentUser.DisplayName}{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            currentUser.Id, currentUser.DisplayName);

        try
        {
            await _emailService.SendSignupRejectedAsync(
                profile.User.Email ?? string.Empty,
                profile.User.DisplayName,
                reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send signup rejection email to {UserId}", userId);
        }

        _logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, currentUser.Id);

        TempData["SuccessMessage"] = _localizer["OnboardingReview_Rejected"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("BoardVoting")]
    [Authorize(Roles = $"{RoleNames.Board},{RoleNames.Admin}")]
    public async Task<IActionResult> BoardVoting()
    {
        var applications = await _dbContext.Applications
            .Include(a => a.User)
            .Include(a => a.BoardVotes)
            .Where(a => a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview)
            .OrderBy(a => a.MembershipTier)
            .ThenBy(a => a.SubmittedAt)
            .ToListAsync();

        var now = _clock.GetCurrentInstant();
        var boardMemberIds = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.RoleName == RoleNames.Board &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync();

        var boardUsers = await _dbContext.Users
            .AsNoTracking()
            .Where(u => boardMemberIds.Contains(u.Id))
            .ToListAsync();

        var viewModel = new BoardVotingDashboardViewModel
        {
            BoardMembers = boardUsers
                .Select(u => new BoardVoteMemberViewModel
                {
                    UserId = u.Id,
                    DisplayName = u.DisplayName
                })
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Applications = applications.Select(a =>
            {
                var appVm = new BoardVotingApplicationViewModel
                {
                    ApplicationId = a.Id,
                    UserId = a.UserId,
                    DisplayName = a.User.DisplayName,
                    ProfilePictureUrl = a.User.ProfilePictureUrl,
                    MembershipTier = a.MembershipTier,
                    ApplicationMotivation = a.Motivation,
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc(),
                    Status = a.Status
                };
                foreach (var vote in a.BoardVotes)
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
    [Authorize(Roles = $"{RoleNames.Board},{RoleNames.Admin}")]
    public async Task<IActionResult> BoardVotingDetail(Guid applicationId)
    {
        var application = await _dbContext.Applications
            .Include(a => a.User)
                .ThenInclude(u => u.Profile)
            .Include(a => a.BoardVotes)
                .ThenInclude(v => v.BoardMemberUser)
            .FirstOrDefaultAsync(a => a.Id == applicationId);

        if (application == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return NotFound();
        }

        var currentVote = application.BoardVotes.FirstOrDefault(v => v.BoardMemberUserId == currentUser.Id);
        var isBoard = User.IsInRole(RoleNames.Board);
        var isAdmin = User.IsInRole(RoleNames.Admin);

        var viewModel = new BoardVotingDetailViewModel
        {
            ApplicationId = application.Id,
            UserId = application.UserId,
            DisplayName = application.User.DisplayName,
            ProfilePictureUrl = application.User.ProfilePictureUrl,
            Email = application.User.Email ?? string.Empty,
            FirstName = application.User.Profile?.FirstName ?? string.Empty,
            LastName = application.User.Profile?.LastName ?? string.Empty,
            City = application.User.Profile?.City,
            CountryCode = application.User.Profile?.CountryCode,
            MembershipTier = application.MembershipTier,
            Status = application.Status,
            ApplicationMotivation = application.Motivation,
            AdditionalInfo = application.AdditionalInfo,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            Votes = application.BoardVotes
                .Select(v => new BoardVoteDetailItemViewModel
                {
                    BoardMemberUserId = v.BoardMemberUserId,
                    DisplayName = v.BoardMemberUser.DisplayName,
                    Vote = v.Vote,
                    Note = v.Note
                })
                .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CurrentUserVote = currentVote?.Vote,
            CurrentUserNote = currentVote?.Note,
            CanFinalize = isBoard || isAdmin
        };

        return View(viewModel);
    }

    [HttpPost("BoardVoting/Vote")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = RoleNames.Board)]
    public async Task<IActionResult> Vote(Guid applicationId, VoteChoice vote, string? note)
    {
        var application = await _dbContext.Applications
            .FirstOrDefaultAsync(a => a.Id == applicationId);

        if (application == null)
        {
            return NotFound();
        }

        if (application.Status != ApplicationStatus.Submitted && application.Status != ApplicationStatus.UnderReview)
        {
            TempData["ErrorMessage"] = _localizer["BoardVoting_ApplicationNotVotable"].Value;
            return RedirectToAction(nameof(BoardVoting));
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return NotFound();
        }

        // Start review if still in Submitted status
        if (application.Status == ApplicationStatus.Submitted)
        {
            application.StartReview(currentUser.Id, _clock);
        }

        var existingVote = await _dbContext.BoardVotes
            .FirstOrDefaultAsync(v => v.ApplicationId == applicationId && v.BoardMemberUserId == currentUser.Id);

        var now = _clock.GetCurrentInstant();

        if (existingVote != null)
        {
            existingVote.Vote = vote;
            existingVote.Note = note;
            existingVote.UpdatedAt = now;
        }
        else
        {
            _dbContext.BoardVotes.Add(new BoardVote
            {
                Id = Guid.NewGuid(),
                ApplicationId = applicationId,
                BoardMemberUserId = currentUser.Id,
                Vote = vote,
                Note = note,
                VotedAt = now
            });
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Board member {UserId} voted {Vote} on application {ApplicationId}",
            currentUser.Id, vote, applicationId);

        TempData["SuccessMessage"] = _localizer["BoardVoting_VoteSaved"].Value;
        return RedirectToAction(nameof(BoardVotingDetail), new { applicationId });
    }

    [HttpPost("BoardVoting/Finalize")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = $"{RoleNames.Board},{RoleNames.Admin}")]
    public async Task<IActionResult> Finalize(BoardVotingFinalizeModel model)
    {
        var application = await _dbContext.Applications
            .Include(a => a.User)
                .ThenInclude(u => u.Profile)
            .Include(a => a.BoardVotes)
            .FirstOrDefaultAsync(a => a.Id == model.ApplicationId);

        if (application == null)
        {
            return NotFound();
        }

        if (application.Status != ApplicationStatus.UnderReview && application.Status != ApplicationStatus.Submitted)
        {
            TempData["ErrorMessage"] = _localizer["BoardVoting_ApplicationNotVotable"].Value;
            return RedirectToAction(nameof(BoardVoting));
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return NotFound();
        }

        // Ensure the application is in UnderReview before finalizing
        if (application.Status == ApplicationStatus.Submitted)
        {
            application.StartReview(currentUser.Id, _clock);
        }

        // Parse meeting date
        LocalDate? meetingDate = null;
        if (!string.IsNullOrWhiteSpace(model.BoardMeetingDate))
        {
            var pattern = NodaTime.Text.LocalDatePattern.Iso;
            var result = pattern.Parse(model.BoardMeetingDate);
            if (result.Success)
            {
                meetingDate = result.Value;
            }
        }

        application.BoardMeetingDate = meetingDate;
        application.DecisionNote = model.DecisionNote;

        if (model.Approved)
        {
            application.Approve(currentUser.Id, model.DecisionNote, _clock);

            // Compute term expiry: next Dec 31 of an odd year that is at least 2 years from now
            var today = _clock.GetCurrentInstant().InUtc().Date;
            application.TermExpiresAt = TermExpiryCalculator.ComputeTermExpiry(today);

            // Update Profile membership tier
            var profile = application.User.Profile;
            if (profile != null)
            {
                profile.MembershipTier = application.MembershipTier;
                profile.UpdatedAt = _clock.GetCurrentInstant();
            }

            await _auditLogService.LogAsync(
                AuditAction.TierApplicationApproved, "Application", application.Id,
                $"{application.MembershipTier} application approved for {application.User.DisplayName} by {currentUser.DisplayName}",
                currentUser.Id, currentUser.DisplayName);

            // Delete individual BoardVote records (GDPR data minimization)
            _dbContext.BoardVotes.RemoveRange(application.BoardVotes);

            // Save before sync — sync queries DB with AsNoTracking and needs persisted state
            await _dbContext.SaveChangesAsync();

            // Sync team membership (must run after SaveChanges)
            if (application.MembershipTier == MembershipTier.Colaborador)
            {
                await _syncJob.SyncColaboradorsMembershipForUserAsync(application.UserId, CancellationToken.None);
            }
            else if (application.MembershipTier == MembershipTier.Asociado)
            {
                await _syncJob.SyncAsociadosMembershipForUserAsync(application.UserId, CancellationToken.None);
            }
        }
        else
        {
            application.Reject(currentUser.Id, model.DecisionNote ?? string.Empty, _clock);

            await _auditLogService.LogAsync(
                AuditAction.TierApplicationRejected, "Application", application.Id,
                $"{application.MembershipTier} application rejected for {application.User.DisplayName} by {currentUser.DisplayName}",
                currentUser.Id, currentUser.DisplayName);

            // Delete individual BoardVote records (GDPR data minimization)
            _dbContext.BoardVotes.RemoveRange(application.BoardVotes);

            await _dbContext.SaveChangesAsync();
        }

        _logger.LogInformation("Application {ApplicationId} finalized as {Decision} by {UserId}",
            application.Id, model.Approved ? "Approved" : "Rejected", currentUser.Id);

        TempData["SuccessMessage"] = _localizer["BoardVoting_Finalized"].Value;
        return RedirectToAction(nameof(BoardVoting));
    }

    private static OnboardingReviewItemViewModel MapToItem(Profile profile, HashSet<Guid> pendingAppUserIds)
    {
        return new OnboardingReviewItemViewModel
        {
            UserId = profile.UserId,
            DisplayName = profile.User.DisplayName,
            ProfilePictureUrl = profile.User.ProfilePictureUrl,
            Email = profile.User.Email ?? string.Empty,
            ConsentCheckStatus = profile.ConsentCheckStatus ?? ConsentCheckStatus.Pending,
            MembershipTier = profile.MembershipTier,
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            HasPendingApplication = pendingAppUserIds.Contains(profile.UserId)
        };
    }
}
