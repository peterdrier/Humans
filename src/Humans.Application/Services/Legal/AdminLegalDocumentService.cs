using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Teams;

namespace Humans.Application.Services.Legal;

/// <summary>
/// Application-layer implementation of <see cref="IAdminLegalDocumentService"/>.
/// Routes all persistence through <see cref="ILegalDocumentRepository"/>
/// and cross-domain team reads through <see cref="ITeamService"/>.
/// </summary>
public sealed partial class AdminLegalDocumentService : IAdminLegalDocumentService
{
    private readonly ILegalDocumentRepository _repository;
    private readonly ILegalDocumentSyncService _legalDocumentSyncService;
    private readonly ITeamService _teamService;
    private readonly GitHubSettings _githubSettings;
    private readonly IClock _clock;

    public AdminLegalDocumentService(
        ILegalDocumentRepository repository,
        ILegalDocumentSyncService legalDocumentSyncService,
        ITeamService teamService,
        IOptions<GitHubSettings> githubSettings,
        IClock clock)
    {
        _repository = repository;
        _legalDocumentSyncService = legalDocumentSyncService;
        _teamService = teamService;
        _githubSettings = githubSettings.Value;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default)
    {
        var documents = await _repository.GetDocumentsAsync(teamId, cancellationToken);
        if (documents.Count == 0)
            return [];

        var now = _clock.GetCurrentInstant();

        // Stitch team names in memory instead of the old .Include(d => d.Team).
        var teamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await _teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);

        return documents
            .Select(d =>
            {
                var teamName = teams.TryGetValue(d.TeamId, out var team) ? team.Name : string.Empty;
                var currentVersion = d.Versions
                    .Where(v => v.EffectiveFrom <= now)
                    .MaxBy(v => v.EffectiveFrom);

                Instant? lastSyncedAt = d.LastSyncedAt == default ? null : d.LastSyncedAt;

                return new AdminLegalDocumentListItem(
                    d.Id,
                    d.Name,
                    d.TeamId,
                    teamName,
                    d.IsRequired,
                    d.IsActive,
                    d.GracePeriodDays,
                    d.GitHubFolderPath,
                    currentVersion?.VersionNumber,
                    lastSyncedAt,
                    d.Versions.Count);
            })
            .OrderBy(item => item.TeamName, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
    }

    public Task<LegalDocument?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(documentId, cancellationToken);

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

        return await _repository.AddAsync(document, cancellationToken);
    }

    public async Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var updated = await _repository.UpdateAsync(
            documentId,
            request.Name,
            request.TeamId,
            request.IsRequired,
            request.IsActive,
            request.GracePeriodDays,
            request.GitHubFolderPath,
            cancellationToken);

        return updated
            ? await _repository.GetByIdAsync(documentId, cancellationToken)
            : null;
    }

    public Task<LegalDocument?> ArchiveLegalDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        _repository.ArchiveAsync(documentId, cancellationToken);

    public Task<string?> SyncLegalDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        _legalDocumentSyncService.SyncDocumentAsync(documentId, cancellationToken);

    public Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken cancellationToken = default) =>
        _repository.UpdateVersionSummaryAsync(documentId, versionId, changesSummary, cancellationToken);

    public Task<int> GetActiveRequiredDocumentCountAsync(CancellationToken cancellationToken = default) =>
        _repository.CountActiveRequiredAsync(cancellationToken);

    [GeneratedRegex(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/tree/(?<branch>[^/]+)/(?<path>[^\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex GitHubUrlPattern();
}
