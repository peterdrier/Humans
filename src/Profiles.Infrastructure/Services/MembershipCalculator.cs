using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Service for computing membership status.
/// </summary>
public class MembershipCalculator : IMembershipCalculator
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IConsentRecordRepository _consentRepository;
    private readonly IClock _clock;

    public MembershipCalculator(
        ProfilesDbContext dbContext,
        IConsentRecordRepository consentRepository,
        IClock clock)
    {
        _dbContext = dbContext;
        _consentRepository = consentRepository;
        _clock = clock;
    }

    public async Task<MembershipStatus> ComputeStatusAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (profile == null)
        {
            return MembershipStatus.None;
        }

        if (profile.IsSuspended)
        {
            return MembershipStatus.Suspended;
        }

        var hasActiveRoles = await HasActiveRolesAsync(userId, cancellationToken);
        if (!hasActiveRoles)
        {
            return MembershipStatus.None;
        }

        var hasAllConsents = await HasAllRequiredConsentsAsync(userId, cancellationToken);
        if (!hasAllConsents)
        {
            return MembershipStatus.Inactive;
        }

        return MembershipStatus.Active;
    }

    public async Task<bool> HasAllRequiredConsentsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var missingConsents = await GetMissingConsentVersionsAsync(userId, cancellationToken);
        return missingConsents.Count == 0;
    }

    public async Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Get current versions of all required documents
        var requiredVersionIds = await _dbContext.LegalDocuments
            .AsNoTracking()
            .Where(d => d.IsRequired && d.IsActive)
            .SelectMany(d => d.Versions)
            .Where(v => v.EffectiveFrom <= _clock.GetCurrentInstant())
            .GroupBy(v => v.LegalDocumentId)
            .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First().Id)
            .ToListAsync(cancellationToken);

        // Get versions the user has consented to
        var consentedVersionIds = await _consentRepository.GetConsentedVersionIdsAsync(userId, cancellationToken);

        // Find missing consents
        return requiredVersionIds
            .Where(id => !consentedVersionIds.Contains(id))
            .ToList();
    }

    public async Task<bool> HasActiveRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        return await _dbContext.RoleAssignments
            .AnyAsync(
                ra => ra.UserId == userId &&
                      ra.ValidFrom <= now &&
                      (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetUsersRequiringStatusUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        // Get all users with active roles
        var now = _clock.GetCurrentInstant();

        var usersWithActiveRoles = await _dbContext.RoleAssignments
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var usersNeedingUpdate = new List<Guid>();

        foreach (var userId in usersWithActiveRoles)
        {
            var hasAllConsents = await HasAllRequiredConsentsAsync(userId, cancellationToken);
            if (!hasAllConsents)
            {
                usersNeedingUpdate.Add(userId);
            }
        }

        return usersNeedingUpdate;
    }
}
