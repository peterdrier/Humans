using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Octokit;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for syncing legal documents from a GitHub repository.
/// </summary>
public class LegalDocumentSyncService : ILegalDocumentSyncService
{
    private readonly HumansDbContext _dbContext;
    private readonly GitHubClient _gitHubClient;
    private readonly GitHubSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<LegalDocumentSyncService> _logger;

    public LegalDocumentSyncService(
        HumansDbContext dbContext,
        IOptions<GitHubSettings> settings,
        IClock clock,
        ILogger<LegalDocumentSyncService> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;

        _gitHubClient = new GitHubClient(new ProductHeaderValue("NobodiesHumans"));

        if (!string.IsNullOrEmpty(_settings.AccessToken))
        {
            _gitHubClient.Credentials = new Credentials(_settings.AccessToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting sync of all legal documents from {Owner}/{Repo}",
            _settings.Owner, _settings.Repository);

        var updatedDocuments = new List<LegalDocument>();

        // Get all configured document types
        foreach (var (documentTypeStr, pathConfig) in _settings.Documents)
        {
            if (!Enum.TryParse<DocumentType>(documentTypeStr, out var documentType))
            {
                _logger.LogWarning("Unknown document type in configuration: {Type}", documentTypeStr);
                continue;
            }

            try
            {
                var document = await SyncDocumentTypeAsync(documentType, pathConfig, cancellationToken);
                if (document != null)
                {
                    updatedDocuments.Add(document);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing document type {Type}", documentType);
            }
        }

        return updatedDocuments;
    }

    /// <inheritdoc />
    public async Task<bool> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await _dbContext.LegalDocuments
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document == null)
        {
            _logger.LogWarning("Document {DocumentId} not found", documentId);
            return false;
        }

        var documentTypeStr = document.Type.ToString();
        if (!_settings.Documents.TryGetValue(documentTypeStr, out var pathConfig))
        {
            _logger.LogWarning("No path configuration for document type {Type}", document.Type);
            return false;
        }

        var updated = await SyncDocumentTypeAsync(document.Type, pathConfig, cancellationToken);
        return updated != null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var documentsWithUpdates = new List<LegalDocument>();

        var documents = await _dbContext.LegalDocuments
            .AsNoTracking()
            .Where(d => d.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var document in documents)
        {
            var documentTypeStr = document.Type.ToString();
            if (!_settings.Documents.TryGetValue(documentTypeStr, out var pathConfig))
            {
                continue;
            }

            try
            {
                var latestSha = await GetLatestCommitShaAsync(pathConfig.SpanishPath, cancellationToken);
                if (latestSha != null && !string.Equals(latestSha, document.CurrentCommitSha, StringComparison.Ordinal))
                {
                    documentsWithUpdates.Add(document);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking for updates to {DocumentType}", document.Type);
            }
        }

        return documentsWithUpdates;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .Where(d => d.IsActive)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentVersion>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        // Get current versions of required documents
        var requiredDocuments = await _dbContext.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .Where(d => d.IsActive && d.IsRequired)
            .ToListAsync(cancellationToken);

        return requiredDocuments
            .Select(d => d.Versions
                .Where(v => v.EffectiveFrom <= now)
                .MaxBy(v => v.EffectiveFrom))
            .Where(v => v != null)
            .Cast<DocumentVersion>()
            .ToList();
    }

    /// <inheritdoc />
    public async Task<DocumentVersion?> GetVersionByIdAsync(
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.DocumentVersions
            .AsNoTracking()
            .Include(v => v.LegalDocument)
            .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
    }

    private async Task<LegalDocument?> SyncDocumentTypeAsync(
        DocumentType documentType,
        DocumentPathConfig pathConfig,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing document type {Type} from {Path}", documentType, pathConfig.SpanishPath);

        // Get or create the document record
        var document = await _dbContext.LegalDocuments
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Type == documentType, cancellationToken);

        var isNew = document == null;
        var now = _clock.GetCurrentInstant();

        if (isNew)
        {
            document = new LegalDocument
            {
                Id = Guid.NewGuid(),
                Type = documentType,
                Name = pathConfig.Name,
                GitHubPath = pathConfig.SpanishPath,
                IsRequired = pathConfig.IsRequired,
                IsActive = true,
                CreatedAt = now
            };
            _dbContext.LegalDocuments.Add(document);
        }

        // Fetch content from GitHub
        string spanishContent;
        string englishContent;
        string commitSha;

        try
        {
            var spanishFile = await GetFileContentAsync(pathConfig.SpanishPath, cancellationToken);
            spanishContent = spanishFile.Content;
            commitSha = spanishFile.Sha;

            // English is optional - use Spanish as fallback
            try
            {
                var englishFile = await GetFileContentAsync(pathConfig.EnglishPath, cancellationToken);
                englishContent = englishFile.Content;
            }
            catch (NotFoundException)
            {
                _logger.LogDebug("No English version found for {Type}, using Spanish", documentType);
                englishContent = spanishContent;
            }
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "Spanish document not found at {Path}", pathConfig.SpanishPath);
            return null;
        }

        // Check if content has changed
        if (!isNew && string.Equals(document!.CurrentCommitSha, commitSha, StringComparison.Ordinal))
        {
            _logger.LogDebug("Document {Type} is up to date (SHA: {Sha})", documentType, commitSha);
            document.LastSyncedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        // Create new version
        var versionNumber = $"v{document!.Versions.Count + 1}.0";
        var newVersion = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = document.Id,
            VersionNumber = versionNumber,
            CommitSha = commitSha,
            ContentSpanish = spanishContent,
            ContentEnglish = englishContent,
            EffectiveFrom = now,
            RequiresReConsent = !isNew, // Require re-consent for updates, not initial version
            CreatedAt = now,
            ChangesSummary = isNew ? "Initial version" : "Updated from GitHub"
        };

        document.Versions.Add(newVersion);
        document.CurrentCommitSha = commitSha;
        document.LastSyncedAt = now;
        document.Name = pathConfig.Name;
        document.GitHubPath = pathConfig.SpanishPath;
        document.IsRequired = pathConfig.IsRequired;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Synced document {Type} version {Version} (SHA: {Sha})",
            documentType, versionNumber, commitSha);

        return document;
    }

    private async Task<(string Content, string Sha)> GetFileContentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var contents = await _gitHubClient.Repository.Content.GetAllContentsByRef(
            _settings.Owner,
            _settings.Repository,
            path,
            _settings.Branch);

        var file = contents.FirstOrDefault()
            ?? throw new NotFoundException("File not found", System.Net.HttpStatusCode.NotFound);

        // Content is base64 encoded for files
        var content = file.Content;
        if (string.Equals(file.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Convert.FromBase64String(file.Content);
            content = System.Text.Encoding.UTF8.GetString(bytes);
        }

        return (content, file.Sha);
    }

    private async Task<string?> GetLatestCommitShaAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var commits = await _gitHubClient.Repository.Commit.GetAll(
                _settings.Owner,
                _settings.Repository,
                new CommitRequest { Path = path, Sha = _settings.Branch },
                new ApiOptions { PageCount = 1, PageSize = 1 });

            return commits.FirstOrDefault()?.Sha;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting latest commit SHA for {Path}", path);
            return null;
        }
    }
}
