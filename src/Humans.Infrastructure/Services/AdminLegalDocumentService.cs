using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Handles admin-facing legal document management operations.
/// </summary>
public partial class AdminLegalDocumentService : IAdminLegalDocumentService
{
    private readonly HumansDbContext _dbContext;
    private readonly ILegalDocumentSyncService _legalDocumentSyncService;
    private readonly GitHubSettings _githubSettings;
    private readonly IClock _clock;

    public AdminLegalDocumentService(
        HumansDbContext dbContext,
        ILegalDocumentSyncService legalDocumentSyncService,
        IOptions<GitHubSettings> githubSettings,
        IClock clock)
    {
        _dbContext = dbContext;
        _legalDocumentSyncService = legalDocumentSyncService;
        _githubSettings = githubSettings.Value;
        _clock = clock;
    }

    public async Task<IReadOnlyList<LegalDocument>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Team)
            .Include(d => d.Versions)
            .AsQueryable();

        if (teamId.HasValue)
        {
            query = query.Where(d => d.TeamId == teamId.Value);
        }

        return await query
            .OrderBy(d => d.Team.Name)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Team>> GetActiveTeamsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SystemTeamType)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<LegalDocument?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.LegalDocuments
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
    }

    public GitHubFolderPathNormalizationResult NormalizeGitHubFolderPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new GitHubFolderPathNormalizationResult(true, null, null);
        }

        input = input.Trim();
        var match = GitHubUrlPattern().Match(input);

        if (!match.Success)
        {
            return new GitHubFolderPathNormalizationResult(true, input.TrimEnd('/') + "/", null);
        }

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        var branch = match.Groups["branch"].Value;
        var path = match.Groups["path"].Value;

        if (!string.Equals(owner, _githubSettings.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repo, _githubSettings.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubFolderPathNormalizationResult(
                false,
                null,
                $"URL points to {owner}/{repo}, but the configured repository is {_githubSettings.Owner}/{_githubSettings.Repository}.");
        }

        if (!string.Equals(branch, _githubSettings.Branch, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubFolderPathNormalizationResult(
                false,
                null,
                $"URL points to branch '{branch}', but the configured branch is '{_githubSettings.Branch}'.");
        }

        return new GitHubFolderPathNormalizationResult(true, path.TrimEnd('/') + "/", null);
    }

    public async Task<LegalDocument> CreateLegalDocumentAsync(
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            TeamId = request.TeamId,
            IsRequired = request.IsRequired,
            IsActive = request.IsActive,
            GracePeriodDays = request.GracePeriodDays,
            GitHubFolderPath = request.GitHubFolderPath,
            CurrentCommitSha = string.Empty,
            CreatedAt = _clock.GetCurrentInstant()
        };

        _dbContext.LegalDocuments.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return document;
    }

    public async Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.LegalDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return null;
        }

        document.Name = request.Name;
        document.TeamId = request.TeamId;
        document.IsRequired = request.IsRequired;
        document.IsActive = request.IsActive;
        document.GracePeriodDays = request.GracePeriodDays;
        document.GitHubFolderPath = request.GitHubFolderPath;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return document;
    }

    public async Task<LegalDocument?> ArchiveLegalDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.LegalDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return null;
        }

        document.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return document;
    }

    public Task<string?> SyncLegalDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        return _legalDocumentSyncService.SyncDocumentAsync(documentId, cancellationToken);
    }

    public async Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken cancellationToken = default)
    {
        var version = await _dbContext.Set<DocumentVersion>()
            .FirstOrDefaultAsync(v => v.Id == versionId && v.LegalDocumentId == documentId, cancellationToken);

        if (version is null)
        {
            return false;
        }

        version.ChangesSummary = string.IsNullOrWhiteSpace(changesSummary) ? null : changesSummary.Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    [GeneratedRegex(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/tree/(?<branch>[^/]+)/(?<path>[^\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex GitHubUrlPattern();
}
