using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Governance/Applications")]
public class GovernanceApplicationsController : HumansControllerBase
{
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<GovernanceApplicationsController> _logger;

    public GovernanceApplicationsController(
        IApplicationDecisionService applicationDecisionService,
        IUserService userService,
        IStringLocalizer<SharedResource> localizer,
        ILogger<GovernanceApplicationsController> logger)
        : base(userService)
    {
        _applicationDecisionService = applicationDecisionService;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(user.Id);

        var hasPendingApplication = applications.Any(a =>
            a.Status == ApplicationStatus.Submitted);

        var isApprovedColaborador = applications.Any(a =>
            a.Status == ApplicationStatus.Approved && a.MembershipTier == MembershipTier.Colaborador);

        var viewModel = new ApplicationIndexViewModel
        {
            Applications = applications.Select(a => new ApplicationSummaryViewModel
            {
                Id = a.Id,
                Status = a.Status,
                MembershipTier = a.MembershipTier,
                SubmittedAt = a.SubmittedAt.ToDateTimeUtc(),
                ResolvedAt = a.ResolvedAt?.ToDateTimeUtc(),
                StatusBadgeClass = a.Status.GetBadgeClass()
            }).ToList(),
            CanSubmitNew = !hasPendingApplication,
            IsApprovedColaborador = isApprovedColaborador
        };

        return View("~/Views/Governance/Applications/Index.cshtml", viewModel);
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(user.Id);
        var hasPending = applications.Any(a => a.Status == ApplicationStatus.Submitted);

        if (hasPending)
        {
            SetError(_localizer["Application_AlreadyPending"].Value);
            return RedirectToAction(nameof(Index));
        }

        var isApprovedColaborador = applications.Any(a =>
            a.Status == ApplicationStatus.Approved && a.MembershipTier == MembershipTier.Colaborador);

        return View("~/Views/Governance/Applications/Create.cshtml", new ApplicationCreateViewModel
        {
            MembershipTier = isApprovedColaborador ? MembershipTier.Asociado : MembershipTier.Colaborador
        });
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApplicationCreateViewModel model)
    {
        if (!model.ConfirmAccuracy)
        {
            ModelState.AddModelError(nameof(model.ConfirmAccuracy), _localizer["Application_ConfirmAccuracy"].Value);
        }

        if (!ModelState.IsValid)
        {
            return View("~/Views/Governance/Applications/Create.cshtml", model);
        }

        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        try
        {
            var result = await _applicationDecisionService.SubmitAsync(
                user.Id, model.MembershipTier, model.Motivation,
                model.AdditionalInfo, model.SignificantContribution, model.RoleUnderstanding,
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

            if (!result.Success)
            {
                if (string.Equals(result.ErrorKey, "AlreadyPending", StringComparison.Ordinal))
                    SetError(_localizer["Application_AlreadyPending"].Value);
                else if (string.Equals(result.ErrorKey, "InvalidTier", StringComparison.Ordinal))
                    ModelState.AddModelError(nameof(model.MembershipTier), _localizer["Application_InvalidTier"].Value);
                else if (string.Equals(result.ErrorKey, "SignificantContributionRequired", StringComparison.Ordinal))
                    ModelState.AddModelError(nameof(model.SignificantContribution),
                        _localizer["Application_SignificantContributionRequired"].Value);
                else if (string.Equals(result.ErrorKey, "RoleUnderstandingRequired", StringComparison.Ordinal))
                    ModelState.AddModelError(nameof(model.RoleUnderstanding),
                        _localizer["Application_RoleUnderstandingRequired"].Value);

                if (!ModelState.IsValid)
                    return View("~/Views/Governance/Applications/Create.cshtml", model);

                return RedirectToAction(nameof(Index));
            }

            SetSuccess(_localizer["Application_Submitted"].Value);
            return RedirectToAction(nameof(Details), new { id = result.ApplicationId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit application for user {UserId}", user.Id);
            SetError(_localizer["Application_SubmitError"].Value);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Details/{id:guid}")]
    public async Task<IActionResult> Details(Guid id)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var application = await _applicationDecisionService.GetUserApplicationDetailAsync(id, user.Id);
        if (application is null)
            return NotFound();

        var viewModel = new ApplicationDetailViewModel
        {
            Id = application.Id,
            Status = application.Status,
            Motivation = application.Motivation,
            AdditionalInfo = application.AdditionalInfo,
            SignificantContribution = application.SignificantContribution,
            RoleUnderstanding = application.RoleUnderstanding,
            MembershipTier = application.MembershipTier,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            ReviewStartedAt = application.ReviewStartedAt?.ToDateTimeUtc(),
            ResolvedAt = application.ResolvedAt?.ToDateTimeUtc(),
            ReviewerName = application.ReviewerName,
            ReviewNotes = application.ReviewNotes,
            CanWithdraw = application.Status == ApplicationStatus.Submitted,
            History = application.History
                .Select(h => new ApplicationHistoryViewModel
                {
                    Status = h.Status,
                    ChangedAt = h.ChangedAt.ToDateTimeUtc(),
                    ChangedBy = h.ChangedByDisplayName ?? string.Empty,
                    Notes = h.Notes
                }).ToList()
        };

        return View("~/Views/Governance/Applications/Details.cshtml", viewModel);
    }

    [HttpPost("Withdraw/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        try
        {
            var result = await _applicationDecisionService.WithdrawAsync(id, user.Id);

            if (!result.Success)
            {
                if (string.Equals(result.ErrorKey, "CannotWithdraw", StringComparison.Ordinal))
                    SetError(_localizer["Application_CannotWithdraw"].Value);
                return RedirectToAction(nameof(Details), new { id });
            }

            SetSuccess(_localizer["Application_Withdrawn"].Value);
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to withdraw application {ApplicationId}", id);
            SetError(_localizer["Application_CannotWithdraw"].Value);
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    [HttpGet("Admin")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Admin(string? status, string? tier, int page = 1)
    {
        var pageSize = 20;
        var (items, totalCount) = await _applicationDecisionService.GetFilteredApplicationsAsync(
            status, tier, page, pageSize);

        var applications = items.Select(a => new AdminApplicationViewModel
        {
            Id = a.Id,
            UserId = a.UserId,
            UserEmail = a.UserEmail,
            UserDisplayName = a.UserDisplayName,
            Status = a.Status,
            StatusBadgeClass = a.Status.GetBadgeClass(),
            SubmittedAt = a.SubmittedAt.ToDateTimeUtc(),
            MotivationPreview = a.Motivation.Length > 100 ? a.Motivation[..100] + "..." : a.Motivation,
            MembershipTier = a.MembershipTier
        }).ToList();

        var viewModel = new AdminApplicationListViewModel
        {
            Applications = applications,
            StatusFilter = status,
            TierFilter = tier,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("~/Views/Governance/Applications/Admin.cshtml", viewModel);
    }

    [HttpGet("Admin/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> AdminDetail(Guid id)
    {
        var application = await _applicationDecisionService.GetApplicationDetailAsync(id);

        if (application is null)
        {
            return NotFound();
        }

        var viewModel = new AdminApplicationDetailViewModel
        {
            Id = application.Id,
            UserId = application.UserId,
            UserEmail = application.UserEmail,
            UserDisplayName = application.UserDisplayName,
            UserProfilePictureUrl = application.UserProfilePictureUrl,
            Status = application.Status,
            Motivation = application.Motivation,
            AdditionalInfo = application.AdditionalInfo,
            SignificantContribution = application.SignificantContribution,
            RoleUnderstanding = application.RoleUnderstanding,
            MembershipTier = application.MembershipTier,
            Language = application.Language,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            ReviewStartedAt = application.ReviewStartedAt?.ToDateTimeUtc(),
            ReviewerName = application.ReviewerName,
            ReviewNotes = application.ReviewNotes,
            CanApproveReject = application.Status == ApplicationStatus.Submitted,
            History = application.History
                .Select(h => new ApplicationHistoryViewModel
                {
                    Status = h.Status,
                    ChangedAt = h.ChangedAt.ToDateTimeUtc(),
                    ChangedBy = h.ChangedByDisplayName ?? string.Empty,
                    Notes = h.Notes
                }).ToList()
        };

        return View("~/Views/Governance/Applications/AdminDetail.cshtml", viewModel);
    }
}
