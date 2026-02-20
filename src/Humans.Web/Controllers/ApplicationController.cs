using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Extensions;
using Humans.Web.Models;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Web.Controllers;

[Authorize]
public class ApplicationController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<Domain.Entities.User> _userManager;
    private readonly IEmailService _emailService;
    private readonly HumansMetricsService _metrics;
    private readonly IClock _clock;
    private readonly ILogger<ApplicationController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ApplicationController(
        HumansDbContext dbContext,
        UserManager<Domain.Entities.User> userManager,
        IEmailService emailService,
        HumansMetricsService metrics,
        IClock clock,
        ILogger<ApplicationController> logger,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _emailService = emailService;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
        _localizer = localizer;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var applications = await _dbContext.Applications
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync();

        // Can submit new if no pending/under review applications
        var hasPendingApplication = applications.Any(a =>
            a.Status == ApplicationStatus.Submitted);

        var viewModel = new ApplicationIndexViewModel
        {
            Applications = applications.Select(a => new ApplicationSummaryViewModel
            {
                Id = a.Id,
                Status = a.Status.ToString(),
                MembershipTier = a.MembershipTier,
                SubmittedAt = a.SubmittedAt.ToDateTimeUtc(),
                ResolvedAt = a.ResolvedAt?.ToDateTimeUtc(),
                StatusBadgeClass = a.Status.GetBadgeClass()
            }).ToList(),
            CanSubmitNew = !hasPendingApplication
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        // Check if user already has a pending application
        var hasPending = await _dbContext.Applications
            .AnyAsync(a => a.UserId == user.Id &&
                (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.Submitted));

        if (hasPending)
        {
            TempData["ErrorMessage"] = _localizer["Application_AlreadyPending"].Value;
            return RedirectToAction(nameof(Index));
        }

        return View(new ApplicationCreateViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApplicationCreateViewModel model)
    {
        if (!model.ConfirmAccuracy)
        {
            ModelState.AddModelError(nameof(model.ConfirmAccuracy), _localizer["Application_ConfirmAccuracy"].Value);
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        // Double-check no pending application
        var hasPending = await _dbContext.Applications
            .AnyAsync(a => a.UserId == user.Id &&
                (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.Submitted));

        if (hasPending)
        {
            TempData["ErrorMessage"] = _localizer["Application_AlreadyPending"].Value;
            return RedirectToAction(nameof(Index));
        }

        var now = _clock.GetCurrentInstant();

        // Validate tier is not Volunteer (applications are for Colaborador/Asociado only)
        if (model.MembershipTier == MembershipTier.Volunteer)
        {
            ModelState.AddModelError(nameof(model.MembershipTier), _localizer["Application_InvalidTier"].Value);
            return View(model);
        }

        // Validate Asociado-specific fields
        if (model.MembershipTier == MembershipTier.Asociado)
        {
            if (string.IsNullOrWhiteSpace(model.SignificantContribution))
            {
                ModelState.AddModelError(nameof(model.SignificantContribution),
                    _localizer["Application_SignificantContributionRequired"].Value);
            }
            if (string.IsNullOrWhiteSpace(model.RoleUnderstanding))
            {
                ModelState.AddModelError(nameof(model.RoleUnderstanding),
                    _localizer["Application_RoleUnderstandingRequired"].Value);
            }
            if (!ModelState.IsValid)
            {
                return View(model);
            }
        }

        var application = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            MembershipTier = model.MembershipTier,
            Motivation = model.Motivation,
            AdditionalInfo = model.AdditionalInfo,
            SignificantContribution = model.MembershipTier == MembershipTier.Asociado
                ? model.SignificantContribution : null,
            RoleUnderstanding = model.MembershipTier == MembershipTier.Asociado
                ? model.RoleUnderstanding : null,
            Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            SubmittedAt = now,
            UpdatedAt = now
        };

        _dbContext.Applications.Add(application);
        await _dbContext.SaveChangesAsync();

        try
        {
            await _emailService.SendApplicationSubmittedAsync(application.Id, user.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send application submission notification for {ApplicationId}", application.Id);
        }

        _logger.LogInformation("User {UserId} submitted application {ApplicationId}", user.Id, application.Id);

        TempData["SuccessMessage"] = _localizer["Application_Submitted"].Value;
        return RedirectToAction(nameof(Details), new { id = application.Id });
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var application = await _dbContext.Applications
            .Include(a => a.ReviewedByUser)
            .Include(a => a.StateHistory)
                .ThenInclude(h => h.ChangedByUser)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == user.Id);

        if (application == null)
        {
            return NotFound();
        }

        var viewModel = new ApplicationDetailViewModel
        {
            Id = application.Id,
            Status = application.Status.ToString(),
            Motivation = application.Motivation,
            AdditionalInfo = application.AdditionalInfo,
            SignificantContribution = application.SignificantContribution,
            RoleUnderstanding = application.RoleUnderstanding,
            MembershipTier = application.MembershipTier,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            ReviewStartedAt = application.ReviewStartedAt?.ToDateTimeUtc(),
            ResolvedAt = application.ResolvedAt?.ToDateTimeUtc(),
            ReviewerName = application.ReviewedByUser?.DisplayName,
            ReviewNotes = application.ReviewNotes,
            CanWithdraw = application.Status == ApplicationStatus.Submitted,
            History = application.StateHistory
                .OrderByDescending(h => h.ChangedAt)
                .Select(h => new ApplicationHistoryViewModel
                {
                    Status = h.Status.ToString(),
                    ChangedAt = h.ChangedAt.ToDateTimeUtc(),
                    ChangedBy = h.ChangedByUser.DisplayName,
                    Notes = h.Notes
                }).ToList()
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var application = await _dbContext.Applications
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == user.Id);

        if (application == null)
        {
            return NotFound();
        }

        if (application.Status != ApplicationStatus.Submitted)
        {
            TempData["ErrorMessage"] = _localizer["Application_CannotWithdraw"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        application.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();
        _metrics.RecordApplicationProcessed("withdrawn");

        _logger.LogInformation("User {UserId} withdrew application {ApplicationId}", user.Id, application.Id);

        TempData["SuccessMessage"] = _localizer["Application_Withdrawn"].Value;
        return RedirectToAction(nameof(Index));
    }

}
