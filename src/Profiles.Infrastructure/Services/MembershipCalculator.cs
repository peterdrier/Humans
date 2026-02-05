using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Configuration;
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
    private readonly GitHubSettings _githubSettings;

    public MembershipCalculator(
        ProfilesDbContext dbContext,
        IConsentRecordRepository consentRepository,
        IClock clock,
        IOptions<GitHubSettings> githubSettings)
    {
        _dbContext = dbContext;
        _consentRepository = consentRepository;
        _clock = clock;
        _githubSettings = githubSettings.Value;
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

        var hasExpiredConsents = await HasAnyExpiredConsentsAsync(userId, cancellationToken);
        if (hasExpiredConsents)
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

    public async Task<bool> HasAnyExpiredConsentsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var gracePeriod = Duration.FromDays(_githubSettings.ReConsentGracePeriodDays);

        // Get missing consents that are past their grace period
        var requiredVersions = await GetRequiredDocumentVersionsAsync(cancellationToken);
        var consentedVersionIds = await _consentRepository.GetConsentedVersionIdsAsync(userId, cancellationToken);

        return requiredVersions
            .Where(v => !consentedVersionIds.Contains(v.Id))
            .Any(v => v.EffectiveFrom + gracePeriod <= now);
    }

    public async Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Get current versions of all required documents
        var requiredVersions = await GetRequiredDocumentVersionsAsync(cancellationToken);
        var requiredVersionIds = requiredVersions.Select(v => v.Id).ToList();

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
            .AsNoTracking()
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Use batch method to avoid N+1 queries
        var usersWithAnyExpiredConsents = await GetUsersWithAnyExpiredConsentsAsync(usersWithActiveRoles, cancellationToken);

        return usersWithAnyExpiredConsents.ToList();
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // Get current versions of all required documents
        var requiredVersions = await GetRequiredDocumentVersionsAsync(cancellationToken);
        var requiredVersionIds = requiredVersions.Select(v => v.Id).ToList();

        if (requiredVersionIds.Count == 0)
        {
            // No required documents, all users have "all" consents
            return userIdList.ToHashSet();
        }

        // Get consented version IDs for all users in batch
        var consentsByUser = await _consentRepository.GetConsentedVersionIdsByUsersAsync(userIdList, cancellationToken);

        var requiredSet = requiredVersionIds.ToHashSet();
        var result = new HashSet<Guid>();

        foreach (var userId in userIdList)
        {
            if (consentsByUser.TryGetValue(userId, out var consented) &&
                requiredSet.All(consented.Contains))
            {
                result.Add(userId);
            }
        }

        return result;
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAnyExpiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var now = _clock.GetCurrentInstant();
        var gracePeriod = Duration.FromDays(_githubSettings.ReConsentGracePeriodDays);

        var requiredVersions = await GetRequiredDocumentVersionsAsync(cancellationToken);
        var expiredVersions = requiredVersions
            .Where(v => v.EffectiveFrom + gracePeriod <= now)
            .ToList();

        if (expiredVersions.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var expiredVersionIds = expiredVersions.Select(v => v.Id).ToHashSet();

        // Get consented version IDs for all users in batch
        var consentsByUser = await _consentRepository.GetConsentedVersionIdsByUsersAsync(userIdList, cancellationToken);

        var result = new HashSet<Guid>();
        foreach (var userId in userIdList)
        {
            if (consentsByUser.TryGetValue(userId, out var consented))
            {
                // User has expired consents if any expired version is NOT in their consented list
                if (expiredVersionIds.Any(id => !consented.Contains(id)))
                {
                    result.Add(userId);
                }
            }
            else
            {
                // No consents at all, and there are expired required versions
                result.Add(userId);
            }
        }

        return result;
    }

    private async Task<List<Domain.Entities.DocumentVersion>> GetRequiredDocumentVersionsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.LegalDocuments
            .AsNoTracking()
            .Where(d => d.IsRequired && d.IsActive)
            .SelectMany(d => d.Versions)
            .Where(v => v.EffectiveFrom <= _clock.GetCurrentInstant())
            .GroupBy(v => v.LegalDocumentId)
            .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First())
            .ToListAsync(cancellationToken);
    }
}
