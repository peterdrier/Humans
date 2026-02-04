using Microsoft.EntityFrameworkCore;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Repositories;

/// <summary>
/// Repository for consent records. This is append-only - no updates or deletes.
/// </summary>
public class ConsentRecordRepository : IConsentRecordRepository
{
    private readonly ProfilesDbContext _context;

    public ConsentRecordRepository(ProfilesDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ConsentRecord>> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ConsentRecords
            .AsNoTracking()
            .Where(cr => cr.UserId == userId)
            .OrderByDescending(cr => cr.ConsentedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ConsentRecord?> GetByUserAndVersionAsync(
        Guid userId,
        Guid documentVersionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ConsentRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                cr => cr.UserId == userId && cr.DocumentVersionId == documentVersionId,
                cancellationToken);
    }

    public async Task<bool> HasConsentAsync(
        Guid userId,
        Guid documentVersionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ConsentRecords
            .AnyAsync(
                cr => cr.UserId == userId &&
                      cr.DocumentVersionId == documentVersionId &&
                      cr.ExplicitConsent,
                cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var ids = await _context.ConsentRecords
            .AsNoTracking()
            .Where(cr => cr.UserId == userId && cr.ExplicitConsent)
            .Select(cr => cr.DocumentVersionId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task AddAsync(
        ConsentRecord consentRecord,
        CancellationToken cancellationToken = default)
    {
        await _context.ConsentRecords.AddAsync(consentRecord, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetUsersWithoutConsentAsync(
        Guid documentVersionId,
        CancellationToken cancellationToken = default)
    {
        // Get all active users who don't have a consent record for this version
        var usersWithConsent = _context.ConsentRecords
            .Where(cr => cr.DocumentVersionId == documentVersionId && cr.ExplicitConsent)
            .Select(cr => cr.UserId);

        return await _context.Users
            .Where(u => !usersWithConsent.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }
}
