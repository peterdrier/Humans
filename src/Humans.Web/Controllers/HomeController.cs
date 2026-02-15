using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

public class HomeController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IConfiguration _configuration;

    public HomeController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IMembershipCalculator membershipCalculator,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _membershipCalculator = membershipCalculator;
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

        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(user.Id);

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
            MembershipStatus = membershipSnapshot.Status.ToString(),
            HasProfile = profile != null,
            ProfileComplete = profile != null && !string.IsNullOrEmpty(profile.FirstName),
            PendingConsents = membershipSnapshot.PendingConsentCount,
            TotalRequiredConsents = membershipSnapshot.RequiredConsentCount,
            IsVolunteerMember = membershipSnapshot.IsVolunteerMember,
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

    public IActionResult About()
    {
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
}
