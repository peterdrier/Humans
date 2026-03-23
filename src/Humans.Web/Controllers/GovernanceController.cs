using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;
using Octokit;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("[controller]")]
public class GovernanceController : HumansControllerBase
{
    private readonly ILogger<GovernanceController> _logger;
    private readonly IMemoryCache _cache;
    private readonly GitHubSettings _gitHubSettings;
    private readonly IProfileService _profileService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;

    private static readonly TimeSpan StatutesCacheTtl = TimeSpan.FromHours(1);
    private static readonly Regex LanguageFilePattern = new(
        @"^(?<name>.+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public GovernanceController(
        UserManager<Domain.Entities.User> userManager,
        ILogger<GovernanceController> logger,
        IMemoryCache cache,
        IOptions<GitHubSettings> gitHubSettings,
        IProfileService profileService,
        IApplicationDecisionService applicationDecisionService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock)
        : base(userManager)
    {
        _logger = logger;
        _cache = cache;
        _gitHubSettings = gitHubSettings.Value;
        _profileService = profileService;
        _applicationDecisionService = applicationDecisionService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(user.Id);
        var latestApplication = applications.Count > 0 ? applications[0] : null;

        var statutesContent = await GetStatutesContentAsync();

        // Tier member counts for the sidebar
        var (colaboradorCount, asociadoCount) = await _profileService.GetTierCountsAsync();

        var isApprovedColaborador = applications.Any(a =>
            a.Status == ApplicationStatus.Approved && a.MembershipTier == MembershipTier.Colaborador);

        var viewModel = new GovernanceIndexViewModel
        {
            StatutesContent = statutesContent,
            HasApplication = latestApplication is not null,
            ApplicationStatus = latestApplication?.Status,
            ApplicationTier = latestApplication?.MembershipTier,
            ApplicationSubmittedAt = latestApplication?.SubmittedAt.ToDateTimeUtc(),
            ApplicationResolvedAt = latestApplication?.ResolvedAt?.ToDateTimeUtc(),
            ApplicationStatusBadgeClass = latestApplication?.Status.GetBadgeClass(),
            CanApply = latestApplication is null ||
                latestApplication.Status != ApplicationStatus.Submitted,
            IsApprovedColaborador = isApprovedColaborador,
            ColaboradorCount = colaboradorCount,
            AsociadoCount = asociadoCount
        };

        return View(viewModel);
    }

    /// <summary>
    /// Fetches statutes markdown content from GitHub, cached for 1 hour.
    /// </summary>
    private async Task<Dictionary<string, string>> GetStatutesContentAsync()
    {
        try
        {
            return await _cache.GetOrCreateAsync(CacheKeys.GovernanceStatutes, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StatutesCacheTtl;
                return await FetchStatutesContentAsync();
            }) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch statutes from GitHub");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task<Dictionary<string, string>> FetchStatutesContentAsync()
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

        var content = new Dictionary<string, string>(StringComparer.Ordinal);
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

            if (fileContent.Count > 0 && fileContent[0].Content is not null)
            {
                content[lang] = fileContent[0].Content;
            }
        }

        return content;
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = _clock.GetCurrentInstant();

        var (assignments, totalCount) = await _roleAssignmentService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now);

        var viewModel = new AdminRoleAssignmentListViewModel
        {
            RoleAssignments = assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.User.Email ?? string.Empty,
                UserDisplayName = ra.User.DisplayName,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByUser?.DisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            RoleFilter = role,
            ShowInactive = showInactive,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("~/Views/Shared/Roles.cshtml", viewModel);
    }
}
