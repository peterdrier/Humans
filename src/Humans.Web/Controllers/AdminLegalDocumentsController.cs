using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.BoardOrAdmin)]
[Route("Admin")]
public class AdminLegalDocumentsController : HumansControllerBase
{
    private readonly IAdminLegalDocumentService _adminLegalDocumentService;
    private readonly IClock _clock;
    private readonly ILogger<AdminLegalDocumentsController> _logger;

    public AdminLegalDocumentsController(
        UserManager<User> userManager,
        IAdminLegalDocumentService adminLegalDocumentService,
        IClock clock,
        ILogger<AdminLegalDocumentsController> logger)
        : base(userManager)
    {
        _adminLegalDocumentService = adminLegalDocumentService;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("LegalDocuments")]
    public async Task<IActionResult> LegalDocuments(Guid? teamId)
    {
        var documents = await _adminLegalDocumentService.GetLegalDocumentsAsync(teamId);
        var now = _clock.GetCurrentInstant();

        var viewModel = new LegalDocumentListViewModel
        {
            FilterTeamId = teamId,
            Teams = await GetTeamSelectItems(),
            Documents = documents.Select(d =>
            {
                var currentVersion = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);

                return new LegalDocumentListItemViewModel
                {
                    Id = d.Id,
                    Name = d.Name,
                    TeamName = d.Team.Name,
                    TeamId = d.TeamId,
                    IsRequired = d.IsRequired,
                    IsActive = d.IsActive,
                    GracePeriodDays = d.GracePeriodDays,
                    CurrentVersion = currentVersion?.VersionNumber,
                    LastSyncedAt = d.LastSyncedAt != default ? d.LastSyncedAt.ToDateTimeUtc() : null,
                    VersionCount = d.Versions.Count
                };
            }).ToList()
        };

        return View("~/Views/Admin/LegalDocuments.cshtml", viewModel);
    }

    [HttpGet("LegalDocuments/Create")]
    public async Task<IActionResult> CreateLegalDocument(Guid? teamId)
    {
        var viewModel = new LegalDocumentEditViewModel
        {
            TeamId = teamId ?? Guid.Empty,
            Teams = await GetTeamSelectItems()
        };

        return View("~/Views/Admin/CreateLegalDocument.cshtml", viewModel);
    }

    [HttpPost("LegalDocuments/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLegalDocument(LegalDocumentEditViewModel model)
    {
        var folderPath = NormalizeGitHubFolderPath(model.GitHubFolderPath);

        if (!ModelState.IsValid)
        {
            model.Teams = await GetTeamSelectItems();
            return View("~/Views/Admin/CreateLegalDocument.cshtml", model);
        }

        var document = await _adminLegalDocumentService.CreateLegalDocumentAsync(ToUpsertRequest(model, folderPath));

        var currentUser = await GetCurrentUserAsync();
        _logger.LogInformation("Admin {AdminId} created legal document {DocumentId} ({Name})",
            currentUser?.Id, document.Id, document.Name);

        // Attempt initial sync immediately.
        if (!string.IsNullOrEmpty(document.GitHubFolderPath))
        {
            try
            {
                var result = await _adminLegalDocumentService.SyncLegalDocumentAsync(document.Id);
                SetSuccess(result is not null
                    ? $"Legal document '{document.Name}' created. {result}"
                    : $"Legal document '{document.Name}' created. GitHub content is already up to date.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial sync failed for new document {DocumentId}", document.Id);
                SetSuccess($"Legal document '{document.Name}' created.");
                SetError($"Initial sync failed: {ex.Message}");
            }
        }
        else
        {
            SetSuccess($"Legal document '{document.Name}' created. Set a GitHub Folder Path and sync to add content.");
        }

        return RedirectToAction(nameof(LegalDocuments));
    }

    [HttpGet("LegalDocuments/{id}/Edit")]
    public async Task<IActionResult> EditLegalDocument(Guid id)
    {
        var document = await _adminLegalDocumentService.GetLegalDocumentWithVersionsAsync(id);

        if (document is null)
        {
            return NotFound();
        }

        var now = _clock.GetCurrentInstant();
        var currentVersion = document.Versions
            .Where(v => v.EffectiveFrom <= now)
            .MaxBy(v => v.EffectiveFrom);

        var viewModel = new LegalDocumentEditViewModel
        {
            Id = document.Id,
            Name = document.Name,
            TeamId = document.TeamId,
            IsRequired = document.IsRequired,
            IsActive = document.IsActive,
            GracePeriodDays = document.GracePeriodDays,
            GitHubFolderPath = document.GitHubFolderPath,
            Teams = await GetTeamSelectItems(),
            CurrentVersion = currentVersion?.VersionNumber,
            LastSyncedAt = document.LastSyncedAt != default ? document.LastSyncedAt.ToDateTimeUtc() : null,
            VersionCount = document.Versions.Count,
            Versions = document.Versions
                .OrderByDescending(v => v.EffectiveFrom)
                .Select(v => new DocumentVersionSummaryViewModel
                {
                    Id = v.Id,
                    VersionNumber = v.VersionNumber,
                    CommitSha = v.CommitSha,
                    EffectiveFrom = v.EffectiveFrom.ToDateTimeUtc(),
                    CreatedAt = v.CreatedAt.ToDateTimeUtc(),
                    ChangesSummary = v.ChangesSummary,
                    RequiresReConsent = v.RequiresReConsent,
                    LanguageCount = v.Content.Count,
                    Languages = v.Content.Keys.Order(StringComparer.Ordinal).ToList()
                })
                .ToList()
        };

        return View("~/Views/Admin/EditLegalDocument.cshtml", viewModel);
    }

    [HttpPost("LegalDocuments/{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditLegalDocument(Guid id, LegalDocumentEditViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        var folderPath = NormalizeGitHubFolderPath(model.GitHubFolderPath);

        if (!ModelState.IsValid)
        {
            model.Teams = await GetTeamSelectItems();
            return View("~/Views/Admin/EditLegalDocument.cshtml", model);
        }

        var document = await _adminLegalDocumentService.UpdateLegalDocumentAsync(
            id,
            ToUpsertRequest(model, folderPath));

        if (document is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserAsync();
        _logger.LogInformation("Admin {AdminId} updated legal document {DocumentId}", currentUser?.Id, id);

        SetSuccess($"Legal document '{document.Name}' updated successfully.");
        return RedirectToAction(nameof(LegalDocuments));
    }

    [HttpPost("LegalDocuments/{id}/Archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveLegalDocument(Guid id)
    {
        var document = await _adminLegalDocumentService.ArchiveLegalDocumentAsync(id);
        if (document is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserAsync();
        _logger.LogInformation("Admin {AdminId} archived legal document {DocumentId}", currentUser?.Id, id);

        SetSuccess($"Legal document '{document.Name}' archived.");
        return RedirectToAction(nameof(LegalDocuments));
    }

    [HttpPost("LegalDocuments/{id}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncLegalDocument(Guid id)
    {
        try
        {
            var result = await _adminLegalDocumentService.SyncLegalDocumentAsync(id);
            var currentUser = await GetCurrentUserAsync();
            _logger.LogInformation("Admin {AdminId} triggered sync for legal document {DocumentId}", currentUser?.Id, id);

            SetSuccess(result ?? "Document is already up to date.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing legal document {DocumentId}", id);
            SetError($"Sync failed: {ex.Message}");
        }

        return RedirectToAction(nameof(EditLegalDocument), new { id });
    }

    [HttpPost("LegalDocuments/{id}/Versions/{versionId}/Summary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateVersionSummary(Guid id, Guid versionId, [FromForm] string changesSummary)
    {
        var updated = await _adminLegalDocumentService.UpdateVersionSummaryAsync(id, versionId, changesSummary);
        if (!updated)
        {
            return NotFound();
        }

        SetSuccess("Version summary updated.");
        return RedirectToAction(nameof(EditLegalDocument), new { id });
    }

    private async Task<List<TeamSelectItem>> GetTeamSelectItems()
    {
        var teams = await _adminLegalDocumentService.GetActiveTeamsAsync();
        return teams.Select(t => new TeamSelectItem { Id = t.Id, Name = t.Name }).ToList();
    }

    private string? NormalizeGitHubFolderPath(string? input)
    {
        var result = _adminLegalDocumentService.NormalizeGitHubFolderPath(input);
        if (!result.IsValid && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            ModelState.AddModelError(nameof(LegalDocumentEditViewModel.GitHubFolderPath), result.ErrorMessage);
        }

        return result.NormalizedFolderPath;
    }

    private static AdminLegalDocumentUpsertRequest ToUpsertRequest(
        LegalDocumentEditViewModel model,
        string? normalizedFolderPath)
    {
        return new AdminLegalDocumentUpsertRequest(
            model.Name,
            model.TeamId,
            model.IsRequired,
            model.IsActive,
            model.GracePeriodDays,
            normalizedFolderPath);
    }
}
