using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Infrastructure.Data;
using Profiles.Web.Models;

namespace Profiles.Web.Controllers;

[Authorize]
public class ConsentController : Controller
{
    private readonly ProfilesDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IConsentRecordRepository _consentRepository;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IClock _clock;
    private readonly ILogger<ConsentController> _logger;

    public ConsentController(
        ProfilesDbContext dbContext,
        UserManager<User> userManager,
        IConsentRecordRepository consentRepository,
        IMembershipCalculator membershipCalculator,
        IGoogleSyncService googleSyncService,
        IClock clock,
        ILogger<ConsentController> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _consentRepository = consentRepository;
        _membershipCalculator = membershipCalculator;
        _googleSyncService = googleSyncService;
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

        var now = _clock.GetCurrentInstant();

        // Get all active documents with their current versions
        var documents = await _dbContext.LegalDocuments
            .Where(d => d.IsActive && d.IsRequired)
            .Include(d => d.Versions)
            .ToListAsync();

        // Get user's consent records
        var userConsents = await _dbContext.ConsentRecords
            .Where(c => c.UserId == user.Id)
            .Include(c => c.DocumentVersion)
            .ThenInclude(v => v.LegalDocument)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync();

        var consentedVersionIds = userConsents.Select(c => c.DocumentVersionId).ToHashSet();

        var requiredDocuments = new List<ConsentDocumentViewModel>();

        foreach (var doc in documents)
        {
            var currentVersion = doc.Versions
                .Where(v => v.EffectiveFrom <= now)
                .MaxBy(v => v.EffectiveFrom);

            if (currentVersion != null)
            {
                var consent = userConsents.FirstOrDefault(c => c.DocumentVersionId == currentVersion.Id);

                requiredDocuments.Add(new ConsentDocumentViewModel
                {
                    DocumentVersionId = currentVersion.Id,
                    DocumentName = doc.Name,
                    DocumentType = doc.Type.ToString(),
                    VersionNumber = currentVersion.VersionNumber,
                    EffectiveFrom = currentVersion.EffectiveFrom.ToDateTimeUtc(),
                    HasConsented = consent != null,
                    ConsentedAt = consent?.ConsentedAt.ToDateTimeUtc(),
                    ChangesSummary = currentVersion.ChangesSummary
                });
            }
        }

        var viewModel = new ConsentIndexViewModel
        {
            RequiredDocuments = requiredDocuments.OrderBy(d => d.HasConsented).ThenBy(d => d.DocumentName, StringComparer.Ordinal).ToList(),
            ConsentHistory = userConsents.Take(10).Select(c => new ConsentHistoryViewModel
            {
                DocumentName = c.DocumentVersion.LegalDocument.Name,
                VersionNumber = c.DocumentVersion.VersionNumber,
                ConsentedAt = c.ConsentedAt.ToDateTimeUtc()
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Review(Guid id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var version = await _dbContext.DocumentVersions
            .Include(v => v.LegalDocument)
            .FirstOrDefaultAsync(v => v.Id == id);

        if (version == null)
        {
            return NotFound();
        }

        var hasConsented = await _dbContext.ConsentRecords
            .AnyAsync(c => c.UserId == user.Id && c.DocumentVersionId == id);

        var viewModel = new ConsentDetailViewModel
        {
            DocumentVersionId = version.Id,
            DocumentName = version.LegalDocument.Name,
            DocumentType = version.LegalDocument.Type.ToString(),
            VersionNumber = version.VersionNumber,
            ContentSpanish = version.ContentSpanish,
            ContentEnglish = version.ContentEnglish,
            EffectiveFrom = version.EffectiveFrom.ToDateTimeUtc(),
            ChangesSummary = version.ChangesSummary,
            HasAlreadyConsented = hasConsented
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(ConsentSubmitModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (!model.ExplicitConsent)
        {
            ModelState.AddModelError(string.Empty, "You must check the consent checkbox to continue.");
            return RedirectToAction(nameof(Review), new { id = model.DocumentVersionId });
        }

        var version = await _dbContext.DocumentVersions
            .Include(v => v.LegalDocument)
            .FirstOrDefaultAsync(v => v.Id == model.DocumentVersionId);

        if (version == null)
        {
            return NotFound();
        }

        // Check if already consented
        var existingConsent = await _dbContext.ConsentRecords
            .AnyAsync(c => c.UserId == user.Id && c.DocumentVersionId == model.DocumentVersionId);

        if (existingConsent)
        {
            TempData["InfoMessage"] = "You have already consented to this document.";
            return RedirectToAction(nameof(Index));
        }

        // Create consent record
        var contentHash = ComputeContentHash(version.ContentSpanish);
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        var consentRecord = new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DocumentVersionId = model.DocumentVersionId,
            ConsentedAt = _clock.GetCurrentInstant(),
            IpAddress = ipAddress,
            UserAgent = userAgent.Length > 500 ? userAgent[..500] : userAgent,
            ContentHash = contentHash,
            ExplicitConsent = true
        };

        await _consentRepository.AddAsync(consentRecord);

        _logger.LogInformation(
            "User {UserId} consented to document {DocumentName} version {Version}",
            user.Id, version.LegalDocument.Name, version.VersionNumber);

        // Check if status is now Active (restores access if previously suspended)
        var newStatus = await _membershipCalculator.ComputeStatusAsync(user.Id);
        if (newStatus == Domain.Enums.MembershipStatus.Active)
        {
            // Restore access to Google resources
            await _googleSyncService.RestoreUserToAllTeamsAsync(user.Id);
            _logger.LogInformation("Restored resource access for user {UserId} after compliance", user.Id);
        }

        TempData["SuccessMessage"] = $"Thank you for consenting to {version.LegalDocument.Name}.";
        return RedirectToAction(nameof(Index));
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
