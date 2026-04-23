using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Legal;

/// <summary>
/// EF-backed implementation of <see cref="ILegalDocumentRepository"/>.
/// The only non-test file that touches <c>DbContext.LegalDocuments</c> or
/// <c>DbContext.DocumentVersions</c> after the §15 Legal document migration
/// lands. Uses <see cref="IDbContextFactory{TContext}"/> so the repository
/// can be registered as Singleton.
/// </summary>
public sealed class LegalDocumentRepository : ILegalDocumentRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public LegalDocumentRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads — LegalDocument
    // ==========================================================================

    public async Task<LegalDocument?> GetByIdAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);
    }

    public async Task<IReadOnlyList<LegalDocument>> GetDocumentsAsync(
        Guid? teamId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .AsQueryable();

        if (teamId.HasValue)
        {
            query = query.Where(d => d.TeamId == teamId.Value);
        }

        // Order by document name — callers group/sort by team name in memory
        // after stitching via ITeamService so this repo never .Include(d => d.Team).
        return await query
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .Where(d => d.IsActive)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .Where(d => d.IsActive && d.IsRequired)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamAsync(
        Guid teamId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.LegalDocuments
            .AsNoTracking()
            .Include(d => d.Versions)
            .Where(d => d.IsActive && d.IsRequired && d.TeamId == teamId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LegalDocument>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.LegalDocuments
            .AsNoTracking()
            .Where(d => d.IsActive && d.IsRequired && teamIds.Contains(d.TeamId))
            .Include(d => d.Team)
            .Include(d => d.Versions)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Reads — DocumentVersion
    // ==========================================================================

    public async Task<DocumentVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.DocumentVersions
            .AsNoTracking()
            .Include(v => v.LegalDocument)
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
    }

    // ==========================================================================
    // Writes — LegalDocument
    // ==========================================================================

    public async Task<LegalDocument> AddAsync(LegalDocument document, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.LegalDocuments.Add(document);
        await ctx.SaveChangesAsync(ct);
        ctx.Entry(document).State = EntityState.Detached;
        return document;
    }

    public async Task<bool> UpdateAsync(
        Guid documentId,
        string name,
        Guid teamId,
        bool isRequired,
        bool isActive,
        int gracePeriodDays,
        string? gitHubFolderPath,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var document = await ctx.LegalDocuments.FindAsync([documentId], ct);
        if (document is null) return false;

        document.Name = name;
        document.TeamId = teamId;
        document.IsRequired = isRequired;
        document.IsActive = isActive;
        document.GracePeriodDays = gracePeriodDays;
        document.GitHubFolderPath = gitHubFolderPath;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<LegalDocument?> ArchiveAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var document = await ctx.LegalDocuments.FindAsync([documentId], ct);
        if (document is null) return null;

        document.IsActive = false;
        await ctx.SaveChangesAsync(ct);
        ctx.Entry(document).State = EntityState.Detached;
        return document;
    }

    public async Task<bool> TouchLastSyncedAtAsync(
        Guid documentId, Instant lastSyncedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var document = await ctx.LegalDocuments.FindAsync([documentId], ct);
        if (document is null) return false;

        document.LastSyncedAt = lastSyncedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddVersionAsync(
        Guid documentId,
        DocumentVersion newVersion,
        string commitSha,
        Instant lastSyncedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var document = await ctx.LegalDocuments.FindAsync([documentId], ct);
        if (document is null) return false;

        // Caller sets newVersion.LegalDocumentId during construction (init-only
        // property). Attach via the DbSet so EF picks up the FK and doesn't
        // try to track the disconnected LegalDocument nav.
        ctx.DocumentVersions.Add(newVersion);

        document.CurrentCommitSha = commitSha;
        document.LastSyncedAt = lastSyncedAt;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Writes — DocumentVersion
    // ==========================================================================

    public async Task<bool> UpdateVersionSummaryAsync(
        Guid documentId,
        Guid versionId,
        string? changesSummary,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var version = await ctx.DocumentVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.LegalDocumentId == documentId, ct);

        if (version is null) return false;

        version.ChangesSummary = string.IsNullOrWhiteSpace(changesSummary)
            ? null
            : changesSummary.Trim();

        await ctx.SaveChangesAsync(ct);
        return true;
    }
}
