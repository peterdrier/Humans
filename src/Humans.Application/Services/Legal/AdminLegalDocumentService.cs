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

public sealed partial class AdminLegalDocumentService(
    ILegalDocumentRepository repository,
    ILegalDocumentSyncService legalDocumentSyncService,
    ITeamService teamService,
    IOptions<GitHubSettings> githubSettings,
    IClock clock) : IAdminLegalDocumentService
{
    private readonly GitHubSettings _githubSettings = githubSettings.Value;

    public async Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetDocumentsAsync(teamId, cancellationToken);
        if (documents.Count == 0)
            return [];

        var now = clock.GetCurrentInstant();

        // Stitch team names in memory (no .Include).
        var teamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);

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

    public async Task<AdminLegalDocumentEditDetail?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var document = await repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
            return null;

        return new AdminLegalDocumentEditDetail(
            document.Id,
            document.Name,
            document.TeamId,
            document.IsRequired,
            document.IsActive,
            document.GracePeriodDays,
            document.GitHubFolderPath,
            document.LastSyncedAt,
            document.Versions
                .Select(v => new AdminLegalDocumentVersionDetail(
                    v.Id,
                    v.VersionNumber,
                    v.CommitSha,
                    v.EffectiveFrom,
                    v.CreatedAt,
                    v.ChangesSummary,
                    v.RequiresReConsent,
                    v.Content.Count,
                    v.Content.Keys.Order(StringComparer.Ordinal).ToList()))
                .ToList());
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
            CreatedAt = clock.GetCurrentInstant()
        };

        return await repository.AddAsync(document, cancellationToken);
    }

    public async Task<AdminLegalDocumentCreateResult> CreateLegalDocumentWithInitialSyncAsync(
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var document = await CreateLegalDocumentAsync(request, cancellationToken);

        if (string.IsNullOrWhiteSpace(document.GitHubFolderPath))
        {
            return new AdminLegalDocumentCreateResult(
                document,
                AdminLegalDocumentInitialSyncStatus.NoGitHubFolderPath);
        }

        try
        {
            var syncMessage = await SyncLegalDocumentAsync(document.Id, cancellationToken);
            return new AdminLegalDocumentCreateResult(
                document,
                syncMessage is null
                    ? AdminLegalDocumentInitialSyncStatus.AlreadyCurrent
                    : AdminLegalDocumentInitialSyncStatus.Synced,
                syncMessage);
        }
        catch (Exception ex)
        {
            return new AdminLegalDocumentCreateResult(
                document,
                AdminLegalDocumentInitialSyncStatus.Failed,
                SyncError: ex.Message);
        }
    }

    public async Task<LegalDocument?> UpdateLegalDocumentAsync(
        Guid documentId,
        AdminLegalDocumentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var updated = await repository.UpdateAsync(
            documentId,
            request.Name,
            request.TeamId,
            request.IsRequired,
            request.IsActive,
            request.GracePeriodDays,
            request.GitHubFolderPath,
            cancellationToken);

        return updated
            ? await repository.GetByIdAsync(documentId, cancellationToken)
            : null;
    }

    public Task<LegalDocument?> ArchiveLegalDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        repository.ArchiveAsync(documentId, cancellationToken);

    public Task<string?> SyncLegalDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        legalDocumentSyncService.SyncDocumentAsync(documentId, cancellationToken);

    public Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken cancellationToken = default) =>
        repository.UpdateVersionSummaryAsync(documentId, versionId, changesSummary, cancellationToken);

    [GeneratedRegex(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/tree/(?<branch>[^/]+)/(?<path>[^\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex GitHubUrlPattern();
}
