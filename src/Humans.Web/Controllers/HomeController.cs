using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

public class HomeController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<HomeController> _logger;
    private readonly IConfiguration _configuration;

    public HomeController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        ILogger<HomeController> logger,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return View();
        }

        // Show dashboard for logged in users
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return View();
        }

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var now = _clock.GetCurrentInstant();

        // Get required document versions
        var requiredVersionIds = await _dbContext.LegalDocuments
            .Where(d => d.IsActive && d.IsRequired)
            .SelectMany(d => d.Versions)
            .Where(v => v.EffectiveFrom <= now)
            .GroupBy(v => v.LegalDocumentId)
            .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First().Id)
            .ToListAsync();

        var userConsentedVersionIds = await _dbContext.ConsentRecords
            .Where(c => c.UserId == user.Id)
            .Select(c => c.DocumentVersionId)
            .ToListAsync();

        var pendingConsents = requiredVersionIds.Except(userConsentedVersionIds).Count();

        // Get latest application
        var latestApplication = await _dbContext.Applications
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.SubmittedAt)
            .FirstOrDefaultAsync();

        var hasPendingApp = latestApplication != null &&
            (latestApplication.Status == ApplicationStatus.Submitted ||
             latestApplication.Status == ApplicationStatus.UnderReview);

        var viewModel = new DashboardViewModel
        {
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            MembershipStatus = GetMembershipStatus(profile, pendingConsents),
            HasProfile = profile != null,
            ProfileComplete = profile != null && !string.IsNullOrEmpty(profile.FirstName),
            PendingConsents = pendingConsents,
            TotalRequiredConsents = requiredVersionIds.Count,
            HasPendingApplication = hasPendingApp,
            LatestApplicationStatus = latestApplication?.Status.ToString(),
            LatestApplicationDate = latestApplication?.SubmittedAt.ToDateTimeUtc(),
            MemberSince = user.CreatedAt.ToDateTimeUtc(),
            LastLogin = user.LastLoginAt?.ToDateTimeUtc()
        };

        return View("Dashboard", viewModel);
    }

    public IActionResult Privacy()
    {
        ViewData["AdminEmail"] = _configuration["Email:AdminAddress"];
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("/Home/Error/{statusCode?}")]
    public IActionResult Error(int? statusCode = null)
    {
        if (statusCode == 404)
        {
            return View("Error404");
        }

        return View();
    }

    private static string GetMembershipStatus(Profile? profile, int pendingConsents)
    {
        if (profile == null)
        {
            return "Incomplete";
        }

        if (profile.IsSuspended)
        {
            return "Suspended";
        }

        if (pendingConsents > 0)
        {
            return "Inactive";
        }

        return "Active";
    }
}
