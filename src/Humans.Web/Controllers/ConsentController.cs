using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
public class ConsentController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly SystemTeamSyncJob _systemTeamSyncJob;
    private readonly HumansMetricsService _metrics;
    private readonly IClock _clock;
    private readonly ILogger<ConsentController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ConsentController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IMembershipCalculator membershipCalculator,
        IGoogleSyncService googleSyncService,
        SystemTeamSyncJob systemTeamSyncJob,
        HumansMetricsService metrics,
        IClock clock,
        ILogger<ConsentController> logger,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _membershipCalculator = membershipCalculator;
        _googleSyncService = googleSyncService;
        _systemTeamSyncJob = systemTeamSyncJob;
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

        var now = _clock.GetCurrentInstant();

        // Get all team IDs whose required documents apply to this user
        var userTeamIds = await _membershipCalculator.GetRequiredTeamIdsForUserAsync(user.Id);

        // Get all active required documents for the user's teams
        var documents = await _dbContext.LegalDocuments
            .Where(d => d.IsActive && d.IsRequired && userTeamIds.Contains(d.TeamId))
            .Include(d => d.Team)
            .Include(d => d.Versions)
            .ToListAsync();

        // Get user's consent records
        var userConsents = await _dbContext.ConsentRecords
            .Where(c => c.UserId == user.Id)
            .Include(c => c.DocumentVersion)
            .ThenInclude(v => v.LegalDocument)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync();

        // Group documents by team
        var teamGroups = documents
            .GroupBy(d => d.TeamId)
            .Select(g =>
            {
                var team = g.First().Team;
                var docViewModels = new List<ConsentDocumentViewModel>();

                foreach (var doc in g)
                {
                    var currentVersion = doc.Versions
                        .Where(v => v.EffectiveFrom <= now)
                        .MaxBy(v => v.EffectiveFrom);

                    if (currentVersion != null)
                    {
                        var consent = userConsents.FirstOrDefault(c => c.DocumentVersionId == currentVersion.Id);

                        docViewModels.Add(new ConsentDocumentViewModel
                        {
                            DocumentVersionId = currentVersion.Id,
                            DocumentName = doc.Name,
                            VersionNumber = currentVersion.VersionNumber,
                            EffectiveFrom = currentVersion.EffectiveFrom.ToDateTimeUtc(),
                            HasConsented = consent != null,
                            ConsentedAt = consent?.ConsentedAt.ToDateTimeUtc(),
                            ChangesSummary = currentVersion.ChangesSummary,
                            LastUpdated = doc.LastSyncedAt != default ? doc.LastSyncedAt.ToDateTimeUtc() : null
                        });
                    }
                }

                return new ConsentTeamGroupViewModel
                {
                    TeamId = team.Id,
                    TeamName = team.Name,
                    Documents = docViewModels
                        .OrderBy(d => d.HasConsented)
                        .ThenBy(d => d.DocumentName, StringComparer.Ordinal)
                        .ToList()
                };
            })
            // Teams with pending docs first, then alphabetical
            .OrderBy(tg => tg.AllConsented)
            .ThenBy(tg => tg.TeamName, StringComparer.Ordinal)
            .ToList();

        var viewModel = new ConsentIndexViewModel
        {
            TeamGroups = teamGroups,
            ConsentHistory = userConsents.Take(10).Select(c => new ConsentHistoryViewModel
            {
                DocumentVersionId = c.DocumentVersionId,
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

        var consentRecord = await _dbContext.ConsentRecords
            .FirstOrDefaultAsync(c => c.UserId == user.Id && c.DocumentVersionId == id);

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var viewModel = new ConsentDetailViewModel
        {
            DocumentVersionId = version.Id,
            DocumentName = version.LegalDocument.Name,
            VersionNumber = version.VersionNumber,
            Content = new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            EffectiveFrom = version.EffectiveFrom.ToDateTimeUtc(),
            ChangesSummary = version.ChangesSummary,
            HasAlreadyConsented = consentRecord != null,
            ConsentedByFullName = profile?.FullName,
            ConsentedAt = consentRecord?.ConsentedAt.ToDateTimeUtc()
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
            ModelState.AddModelError(string.Empty, _localizer["Consent_MustCheck"].Value);
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
            TempData["InfoMessage"] = _localizer["Consent_AlreadyConsented"].Value;
            return RedirectToAction(nameof(Index));
        }

        // Create consent record — hash canonical Spanish content
        var canonicalContent = version.Content.GetValueOrDefault("es", string.Empty);
        var contentHash = ComputeContentHash(canonicalContent);
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

        _dbContext.ConsentRecords.Add(consentRecord);
        await _dbContext.SaveChangesAsync();
        _metrics.RecordConsentGiven();

        _logger.LogInformation(
            "User {UserId} consented to document {DocumentName} version {Version}",
            user.Id, version.LegalDocument.Name, version.VersionNumber);

        // Check if user now has all required consents and needs consent check
        var profile = await _dbContext.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (profile != null && !profile.IsApproved && profile.ConsentCheckStatus == null)
        {
            var hasAllConsents = await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
                user.Id, SystemTeamIds.Volunteers);
            if (hasAllConsents)
            {
                profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
                profile.UpdatedAt = _clock.GetCurrentInstant();
                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("User {UserId} has all consents signed, consent check set to Pending", user.Id);
            }
        }

        // Sync system team memberships (adds user if eligible + all consents done)
        await _systemTeamSyncJob.SyncVolunteersMembershipForUserAsync(user.Id);
        await _systemTeamSyncJob.SyncLeadsMembershipForUserAsync(user.Id);

        TempData["SuccessMessage"] = string.Format(_localizer["Consent_ThankYou"].Value, version.LegalDocument.Name);
        return RedirectToAction(nameof(Index));
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
