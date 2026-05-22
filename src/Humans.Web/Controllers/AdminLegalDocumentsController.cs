using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Legal;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Legal/Admin")]
public class AdminLegalDocumentsController(
    IUserService userService,
    IAdminLegalDocumentService adminLegalDocumentService,
    ITeamServiceRead teamService,
    IClock clock,
    ILogger<AdminLegalDocumentsController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Documents")]
    public async Task<IActionResult> LegalDocuments(Guid? teamId)
    {
        var documents = await adminLegalDocumentService.GetLegalDocumentsAsync(teamId);

        var viewModel = new LegalDocumentListViewModel
        {
            FilterTeamId = teamId,
            Teams = await GetTeamSelectItems(),
            Documents = documents.Select(d => new LegalDocumentListItemViewModel
            {
                Id = d.Id,
                Name = d.Name,
                TeamName = d.TeamName,
                TeamId = d.TeamId,
                IsRequired = d.IsRequired,
                IsActive = d.IsActive,
                GracePeriodDays = d.GracePeriodDays,
                CurrentVersion = d.CurrentVersion,
                LastSyncedAt = d.LastSyncedAt?.ToDateTimeUtc(),
                VersionCount = d.VersionCount
            }).ToList()
        };

        return View("~/Views/AdminLegalDocuments/LegalDocuments.cshtml", viewModel);
    }

    [HttpGet("Documents/Create")]
    public async Task<IActionResult> CreateLegalDocument(Guid? teamId)
    {
        var viewModel = new LegalDocumentEditViewModel
        {
            TeamId = teamId ?? Guid.Empty,
            Teams = await GetTeamSelectItems()
        };

        return View("~/Views/AdminLegalDocuments/CreateLegalDocument.cshtml", viewModel);
    }

    [HttpPost("Documents/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLegalDocument(LegalDocumentEditViewModel model)
    {
        var folderPath = NormalizeGitHubFolderPath(model.GitHubFolderPath);

        if (!ModelState.IsValid)
        {
            model.Teams = await GetTeamSelectItems();
            return View("~/Views/AdminLegalDocuments/CreateLegalDocument.cshtml", model);
        }

        var result = await adminLegalDocumentService.CreateLegalDocumentWithInitialSyncAsync(
            ToUpsertRequest(model, folderPath));
        var document = result.Document;

        var currentUser = await GetCurrentUserInfoAsync();
        logger.LogInformation("Admin {AdminId} created legal document {DocumentId} ({Name})",
            currentUser?.Id, document.Id, document.Name);

        if (result.InitialSyncStatus == AdminLegalDocumentInitialSyncStatus.Synced)
        {
            SetSuccess($"Legal document '{document.Name}' created. {result.SyncMessage}");
        }
        else if (result.InitialSyncStatus == AdminLegalDocumentInitialSyncStatus.AlreadyCurrent)
        {
            SetSuccess($"Legal document '{document.Name}' created. GitHub content is already up to date.");
        }
        else if (result.InitialSyncStatus == AdminLegalDocumentInitialSyncStatus.Failed)
        {
            logger.LogWarning("Initial sync failed for new document {DocumentId}: {SyncError}",
                document.Id, result.SyncError);
            SetSuccess($"Legal document '{document.Name}' created.");
            SetError($"Initial sync failed: {result.SyncError}");
        }
        else
        {
            SetSuccess($"Legal document '{document.Name}' created. Set a GitHub Folder Path and sync to add content.");
        }

        return RedirectToAction(nameof(LegalDocuments));
    }

    [HttpGet("Documents/{id}/Edit")]
    public async Task<IActionResult> EditLegalDocument(Guid id)
    {
        var document = await adminLegalDocumentService.GetLegalDocumentWithVersionsAsync(id);

        if (document is null)
        {
            return NotFound();
        }

        var now = clock.GetCurrentInstant();
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
                    LanguageCount = v.LanguageCount,
                    Languages = v.Languages.ToList()
                })
                .ToList()
        };

        return View("~/Views/AdminLegalDocuments/EditLegalDocument.cshtml", viewModel);
    }

    [HttpPost("Documents/{id}/Edit")]
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
            return View("~/Views/AdminLegalDocuments/EditLegalDocument.cshtml", model);
        }

        var document = await adminLegalDocumentService.UpdateLegalDocumentAsync(
            id,
            ToUpsertRequest(model, folderPath));

        if (document is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        logger.LogInformation("Admin {AdminId} updated legal document {DocumentId}", currentUser?.Id, id);

        SetSuccess($"Legal document '{document.Name}' updated successfully.");
        return RedirectToAction(nameof(LegalDocuments));
    }

    [HttpPost("Documents/{id}/Archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveLegalDocument(Guid id)
    {
        var document = await adminLegalDocumentService.ArchiveLegalDocumentAsync(id);
        if (document is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        logger.LogInformation("Admin {AdminId} archived legal document {DocumentId}", currentUser?.Id, id);

        SetSuccess($"Legal document '{document.Name}' archived.");
        return RedirectToAction(nameof(LegalDocuments));
    }

    [HttpPost("Documents/{id}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncLegalDocument(Guid id)
    {
        try
        {
            var result = await adminLegalDocumentService.SyncLegalDocumentAsync(id);
            var currentUser = await GetCurrentUserInfoAsync();
            logger.LogInformation("Admin {AdminId} triggered sync for legal document {DocumentId}", currentUser?.Id, id);

            SetSuccess(result ?? "Document is already up to date.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing legal document {DocumentId}", id);
            SetError($"Sync failed: {ex.Message}");
        }

        return RedirectToAction(nameof(EditLegalDocument), new { id });
    }

    [HttpPost("Documents/{id}/Versions/{versionId}/Summary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateVersionSummary(Guid id, Guid versionId, [FromForm] string changesSummary)
    {
        var updated = await adminLegalDocumentService.UpdateVersionSummaryAsync(id, versionId, changesSummary);
        if (!updated)
        {
            return NotFound();
        }

        SetSuccess("Version summary updated.");
        return RedirectToAction(nameof(EditLegalDocument), new { id });
    }

    private async Task<List<TeamSelectItem>> GetTeamSelectItems()
    {
        var teams = (await teamService.GetTeamsAsync()).Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        return teams.Select(t => new TeamSelectItem { Id = t.Id, Name = t.Name }).ToList();
    }

    private string? NormalizeGitHubFolderPath(string? input)
    {
        var result = adminLegalDocumentService.NormalizeGitHubFolderPath(input);
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
