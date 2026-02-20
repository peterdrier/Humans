using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Octokit;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("[controller]")]
public class GovernanceController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<Domain.Entities.User> _userManager;
    private readonly ILogger<GovernanceController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IMemoryCache _cache;
    private readonly GitHubSettings _gitHubSettings;

    private const string StatutesCacheKey = "StatutesContent";
    private static readonly Regex LanguageFilePattern = new(
        @"^(?<name>.+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public GovernanceController(
        HumansDbContext dbContext,
        UserManager<Domain.Entities.User> userManager,
        ILogger<GovernanceController> logger,
        IStringLocalizer<SharedResource> localizer,
        IMemoryCache cache,
        IOptions<GitHubSettings> gitHubSettings)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _logger = logger;
        _localizer = localizer;
        _cache = cache;
        _gitHubSettings = gitHubSettings.Value;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        var latestApplication = await _dbContext.Applications
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.SubmittedAt)
            .FirstOrDefaultAsync();

        var statutesContent = await GetStatutesContentAsync();

        var viewModel = new GovernanceIndexViewModel
        {
            StatutesContent = statutesContent,
            HasApplication = latestApplication != null,
            ApplicationStatus = latestApplication?.Status.ToString(),
            ApplicationSubmittedAt = latestApplication?.SubmittedAt.ToDateTimeUtc(),
            ApplicationResolvedAt = latestApplication?.ResolvedAt?.ToDateTimeUtc(),
            ApplicationStatusBadgeClass = latestApplication?.Status.GetBadgeClass(),
            CanApply = latestApplication == null ||
                latestApplication.Status != ApplicationStatus.Submitted
        };

        return View(viewModel);
    }

    /// <summary>
    /// Fetches statutes markdown content from GitHub, cached for 1 hour.
    /// </summary>
    private async Task<Dictionary<string, string>> GetStatutesContentAsync()
    {
        if (_cache.TryGetValue(StatutesCacheKey, out Dictionary<string, string>? cached) && cached != null)
            return cached;

        var content = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var client = new GitHubClient(new ProductHeaderValue("NobodiesHumans"));
            if (!string.IsNullOrEmpty(_gitHubSettings.AccessToken))
            {
                client.Credentials = new Credentials(_gitHubSettings.AccessToken);
            }

            var files = await client.Repository.Content.GetAllContents(
                _gitHubSettings.Owner,
                _gitHubSettings.Repository,
                "Estatutos");

            foreach (var file in files.Where(f =>
                f.Name.StartsWith("ESTATUTOS", StringComparison.OrdinalIgnoreCase) &&
                f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            {
                var match = LanguageFilePattern.Match(file.Name);
                if (!match.Success) continue;

                var lang = match.Groups["lang"].Success
                    ? match.Groups["lang"].Value.ToLowerInvariant()
                    : "es";

                // Fetch full content (GetAllContents for a directory only returns metadata)
                var fileContent = await client.Repository.Content.GetAllContents(
                    _gitHubSettings.Owner,
                    _gitHubSettings.Repository,
                    file.Path);

                if (fileContent.Count > 0 && fileContent[0].Content != null)
                {
                    content[lang] = fileContent[0].Content;
                }
            }

            _cache.Set(StatutesCacheKey, content, TimeSpan.FromHours(1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch statutes from GitHub");
        }

        return content;
    }
}
