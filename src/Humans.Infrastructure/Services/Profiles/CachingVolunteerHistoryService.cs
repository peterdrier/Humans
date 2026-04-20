using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;

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
    private readonly IUserEmailRepository _userEmailRepository;

    public CachingVolunteerHistoryService(
        IVolunteerHistoryService inner,
        IProfileStore store,
        IProfileRepository profileRepository,
        IVolunteerHistoryRepository volunteerHistoryRepository,
        IUserService userService,
        IUserEmailRepository userEmailRepository)
    {
        _inner = inner;
        _store = store;
        _profileRepository = profileRepository;
        _volunteerHistoryRepository = volunteerHistoryRepository;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
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
        var userId = _store.GetUserIdByProfileId(profileId);
        if (userId is null)
            return;

        var userIdValue = userId.Value;
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userIdValue, cancellationToken);
        var user = await _userService.GetByIdAsync(userIdValue, cancellationToken);

        if (profile is not null && user is not null)
        {
            var history = await _volunteerHistoryRepository.GetByProfileIdReadOnlyAsync(profileId, cancellationToken);
            var emails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userIdValue, cancellationToken);
            var notificationEmail = emails.FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified)?.Email;

            var cached = CachedProfile.Create(profile, user, history, notificationEmail);
            _store.Upsert(userIdValue, cached);
        }
    }
}
