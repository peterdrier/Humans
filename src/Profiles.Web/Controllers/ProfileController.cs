using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Domain.Entities;
using Profiles.Infrastructure.Data;
using Profiles.Web.Models;

namespace Profiles.Web.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly ProfilesDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<ProfileController> _logger;
    private readonly IConfiguration _configuration;

    public ProfileController(
        ProfilesDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ProfileController> logger,
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
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        // Get consent status
        var requiredVersions = await _dbContext.DocumentVersions
            .Where(v => v.RequiresReConsent || v.LegalDocument.Versions
                .OrderByDescending(dv => dv.EffectiveFrom)
                .First().Id == v.Id)
            .Select(v => v.Id)
            .ToListAsync();

        var userConsents = await _dbContext.ConsentRecords
            .Where(c => c.UserId == user.Id)
            .Select(c => c.DocumentVersionId)
            .ToListAsync();

        var pendingConsents = requiredVersions.Except(userConsents).Count();

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            PhoneCountryCode = profile?.PhoneCountryCode,
            PhoneNumber = profile?.PhoneNumber,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Bio = profile?.Bio,
            HasPendingConsents = pendingConsents > 0,
            PendingConsentCount = pendingConsents,
            MembershipStatus = profile != null ? ComputeStatus(profile, user.Id).ToString() : "Incomplete"
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Edit()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            PhoneCountryCode = profile?.PhoneCountryCode,
            PhoneNumber = profile?.PhoneNumber,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Latitude = profile?.Latitude,
            Longitude = profile?.Longitude,
            PlaceId = profile?.PlaceId,
            Bio = profile?.Bio
        };

        ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var now = _clock.GetCurrentInstant();

        if (profile == null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Profiles.Add(profile);
        }

        profile.FirstName = model.FirstName;
        profile.LastName = model.LastName;
        profile.PhoneCountryCode = model.PhoneCountryCode;
        profile.PhoneNumber = model.PhoneNumber;
        profile.City = model.City;
        profile.CountryCode = model.CountryCode;
        profile.Latitude = model.Latitude;
        profile.Longitude = model.Longitude;
        profile.PlaceId = model.PlaceId;
        profile.Bio = model.Bio;
        profile.UpdatedAt = now;

        // Update display name on user
        user.DisplayName = $"{model.FirstName} {model.LastName}".Trim();
        await _userManager.UpdateAsync(user);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} updated their profile", user.Id);

        TempData["SuccessMessage"] = "Profile updated successfully.";
        return RedirectToAction(nameof(Index));
    }

    private string ComputeStatus(Profile profile, Guid userId)
    {
        if (profile.IsSuspended)
        {
            return "Suspended";
        }

        // Simplified status check - full implementation would use IMembershipCalculator
        return "Active";
    }
}
