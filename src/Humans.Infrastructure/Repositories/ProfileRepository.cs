using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IProfileRepository"/>. The only
/// non-test file that touches <c>DbContext.Profiles</c> or
/// <c>DbContext.ProfileLanguages</c> after the Profile migration lands.
/// </summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly HumansDbContext _dbContext;

    public ProfileRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public async Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, Profile>();

        var list = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync(ct);

        return list.ToDictionary(p => p.UserId);
    }

    public async Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) =>
        await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.VolunteerHistory)
            .Include(p => p.Languages)
            .AsSplitQuery()
            .ToListAsync(ct);

    public async Task<(byte[]? Data, string? ContentType)> GetProfilePictureDataAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var data = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new { p.ProfilePictureData, p.ProfilePictureContentType })
            .FirstOrDefaultAsync(ct);

        return (data?.ProfilePictureData, data?.ProfilePictureContentType);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
            return [];

        return await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => userIdList.Contains(p.UserId) && p.ProfilePictureData != null)
            .Select(p => new { p.Id, p.UserId, p.UpdatedAt })
            .AsAsyncEnumerable()
            .Select(p => (p.Id, p.UserId, p.UpdatedAt.ToUnixTimeTicks()))
            .ToListAsync(ct);
    }

    public async Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(
        CancellationToken ct = default)
    {
        var colaboradorCount = await _dbContext.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Colaborador && !p.IsSuspended, ct);
        var asociadoCount = await _dbContext.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Asociado && !p.IsSuspended, ct);

        return (colaboradorCount, asociadoCount);
    }

    public async Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
        Guid profileId, CancellationToken ct = default) =>
        await _dbContext.ProfileLanguages
            .AsNoTracking()
            .Where(pl => pl.ProfileId == profileId)
            .OrderByDescending(pl => pl.Proficiency)
            .ThenBy(pl => pl.LanguageCode)
            .ToListAsync(ct);

    public async Task ReplaceLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        var existing = await _dbContext.ProfileLanguages
            .Where(pl => pl.ProfileId == profileId)
            .ToListAsync(ct);
        _dbContext.ProfileLanguages.RemoveRange(existing);

        if (languages.Count > 0)
            _dbContext.ProfileLanguages.AddRange(languages);

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task AddAsync(Profile profile, CancellationToken ct = default)
    {
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync(ct);
    }

    public Task UpdateAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);
}
