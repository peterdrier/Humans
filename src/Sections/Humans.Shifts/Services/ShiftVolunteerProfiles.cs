using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftVolunteerProfiles(
    IShiftManagementRepository repo,
    IClock clock,
    ILogger<ShiftManagementService> logger) : IApplicationService
{
    public async Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null, CancellationToken ct = default)
    {
        var tags = await repo.GetTagsAsync(query, ct);
        return tags
            .Select(tag => new ShiftTagSummary(tag.Id, tag.Name))
            .ToList();
    }

    public async Task<ShiftTagSummary> GetOrCreateTagAsync(string name)
    {
        var trimmed = name.Trim();
        var tag = await repo.GetOrCreateTagAsync(trimmed);
        return new ShiftTagSummary(tag.Id, tag.Name);
    }

    public async Task<bool> SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds)
    {
        var knownIds = (await repo.GetTagsAsync()).Select(tag => tag.Id).ToHashSet();
        if (tagIds.Any(id => !knownIds.Contains(id)))
        {
            logger.LogWarning("Rejected unknown volunteer tag preferences for user {UserId}", userId);
            return false;
        }

        await repo.SetVolunteerTagPreferencesAsync(userId, tagIds);
        return true;
    }

    public async Task UpdateShiftProfileAsync(ShiftVolunteerProfileInfo profile)
    {
        var now = clock.GetCurrentInstant();
        var existing = await repo.GetVolunteerEventProfileAsync(profile.UserId);
        var entity = existing ?? new VolunteerEventProfile
        {
            Id = Guid.NewGuid(),
            UserId = profile.UserId,
            CreatedAt = now
        };

        entity.Skills = [.. profile.Skills];
        entity.Quirks = [.. profile.Quirks];
        entity.Languages = [.. profile.Languages];
        entity.UpdatedAt = now;

        if (existing is null)
            await repo.AddVolunteerEventProfileAsync(entity);
        else
            await repo.UpdateVolunteerEventProfileAsync(entity);
    }

    public async Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await repo.GetVolunteerEventProfileAsync(userId, ct);
        return profile is null
            ? null
            : new ShiftVolunteerProfileInfo(profile.UserId, profile.Skills, profile.Quirks, profile.Languages);
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await repo.ReassignProfilesAndTagPrefsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
    }
}
