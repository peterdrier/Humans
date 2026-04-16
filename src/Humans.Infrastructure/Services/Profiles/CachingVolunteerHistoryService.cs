using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Caching decorator for <see cref="IVolunteerHistoryService"/>. After saves,
/// refreshes the <see cref="IProfileStore"/> entry to keep search results
/// consistent with the latest volunteer history.
/// </summary>
public sealed class CachingVolunteerHistoryService : IVolunteerHistoryService
{
    private readonly IVolunteerHistoryService _inner;
    private readonly IProfileStore _store;
    private readonly IProfileRepository _profileRepository;
    private readonly IVolunteerHistoryRepository _volunteerHistoryRepository;
    private readonly IUserService _userService;

    public CachingVolunteerHistoryService(
        IVolunteerHistoryService inner,
        IProfileStore store,
        IProfileRepository profileRepository,
        IVolunteerHistoryRepository volunteerHistoryRepository,
        IUserService userService)
    {
        _inner = inner;
        _store = store;
        _profileRepository = profileRepository;
        _volunteerHistoryRepository = volunteerHistoryRepository;
        _userService = userService;
    }

    public Task<IReadOnlyList<VolunteerHistoryEntryDto>> GetAllAsync(
        Guid profileId, CancellationToken cancellationToken = default) =>
        _inner.GetAllAsync(profileId, cancellationToken);

    public async Task SaveAsync(
        Guid profileId,
        IReadOnlyList<VolunteerHistoryEntryEditDto> entries,
        CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(profileId, entries, cancellationToken);

        // Refresh the store entry for this profile's owner
        // Find the userId from existing store entries (profileId → userId mapping)
        var entry = _store.GetAll().FirstOrDefault(p => p.ProfileId == profileId);
        if (entry is null)
            return;

        var userId = entry.UserId;
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        var user = await _userService.GetByIdAsync(userId, cancellationToken);

        if (profile is not null && user is not null)
        {
            var history = await _volunteerHistoryRepository.GetByProfileIdReadOnlyAsync(profileId, cancellationToken);

            var cached = new CachedProfile(
                UserId: userId,
                DisplayName: user.DisplayName,
                ProfilePictureUrl: user.ProfilePictureUrl,
                HasCustomPicture: profile.ProfilePictureData is not null,
                ProfileId: profile.Id,
                UpdatedAtTicks: profile.UpdatedAt.ToUnixTimeTicks(),
                BurnerName: profile.BurnerName,
                Bio: profile.Bio,
                Pronouns: profile.Pronouns,
                ContributionInterests: profile.ContributionInterests,
                City: profile.City,
                CountryCode: profile.CountryCode,
                Latitude: profile.Latitude,
                Longitude: profile.Longitude,
                BirthdayDay: profile.DateOfBirth?.Day,
                BirthdayMonth: profile.DateOfBirth?.Month,
                IsApproved: profile.IsApproved,
                IsSuspended: profile.IsSuspended,
                VolunteerHistory: history
                    .Select(v => new CachedVolunteerEntry(v.EventName, v.Description))
                    .ToList());

            _store.Upsert(userId, cached);
        }
    }
}
