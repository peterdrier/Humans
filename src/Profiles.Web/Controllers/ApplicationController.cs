using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;
using Profiles.Web.Extensions;
using Profiles.Web.Models;
using MemberApplication = Profiles.Domain.Entities.Application;

namespace Profiles.Web.Controllers;

[Authorize]
public class ApplicationController : Controller
{
    private readonly ProfilesDbContext _dbContext;
    private readonly UserManager<Domain.Entities.User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<ApplicationController> _logger;

    public ApplicationController(
        ProfilesDbContext dbContext,
        UserManager<Domain.Entities.User> userManager,
        IClock clock,
        ILogger<ApplicationController> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
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
            a.Status == ApplicationStatus.Submitted ||
            a.Status == ApplicationStatus.UnderReview);

        var viewModel = new ApplicationIndexViewModel
        {
            Applications = applications.Select(a => new ApplicationSummaryViewModel
            {
                Id = a.Id,
                Status = a.Status.ToString(),
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
                (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview));

        if (hasPending)
        {
            TempData["ErrorMessage"] = "You already have a pending application.";
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
            ModelState.AddModelError(nameof(model.ConfirmAccuracy), "You must confirm the accuracy of your information.");
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
                (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview));

        if (hasPending)
        {
            TempData["ErrorMessage"] = "You already have a pending application.";
            return RedirectToAction(nameof(Index));
        }

        var now = _clock.GetCurrentInstant();

        var application = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Motivation = model.Motivation,
            AdditionalInfo = model.AdditionalInfo,
            SubmittedAt = now,
            UpdatedAt = now
        };

        _dbContext.Applications.Add(application);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} submitted application {ApplicationId}", user.Id, application.Id);

        TempData["SuccessMessage"] = "Your application has been submitted successfully!";
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
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            ReviewStartedAt = application.ReviewStartedAt?.ToDateTimeUtc(),
            ResolvedAt = application.ResolvedAt?.ToDateTimeUtc(),
            ReviewerName = application.ReviewedByUser?.DisplayName,
            ReviewNotes = application.ReviewNotes,
            CanWithdraw = application.Status == ApplicationStatus.Submitted ||
                          application.Status == ApplicationStatus.UnderReview,
            History = application.StateHistory
                .OrderByDescending(h => h.ChangedAt)
                .Select(h => new ApplicationHistoryViewModel
                {
                    Status = h.Status.ToString(),
                    ChangedAt = h.ChangedAt.ToDateTimeUtc(),
                    ChangedBy = h.ChangedByUser?.DisplayName ?? "System",
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

        if (application.Status != ApplicationStatus.Submitted &&
            application.Status != ApplicationStatus.UnderReview)
        {
            TempData["ErrorMessage"] = "This application cannot be withdrawn.";
            return RedirectToAction(nameof(Details), new { id });
        }

        application.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} withdrew application {ApplicationId}", user.Id, application.Id);

        TempData["SuccessMessage"] = "Your application has been withdrawn.";
        return RedirectToAction(nameof(Index));
    }

}
