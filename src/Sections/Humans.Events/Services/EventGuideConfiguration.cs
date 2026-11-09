using Humans.Base.Interfaces;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using NodaTime;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.Services;

internal sealed class EventGuideConfiguration(
    IEventRepository repo,
    ISettingsService settingsService,
    IClock clock,
    ILogger logger) : IApplicationService
{
    public async Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        return settings is null ? null : await ToGuideSettingsViewAsync(settings, ct);
    }

    private async Task<EventGuideSettingsView> ToGuideSettingsViewAsync(EventGuideSettings settings, CancellationToken ct)
    {
        var burn = await settingsService.GetEventSettingsByIdAsync(settings.EventSettingsId, ct);
        return new EventGuideSettingsView(
            Id: settings.Id,
            EventSettingsId: settings.EventSettingsId,
            SubmissionOpenAt: settings.SubmissionOpenAt,
            SubmissionCloseAt: settings.SubmissionCloseAt,
            GuidePublishAt: settings.GuidePublishAt,
            MaxPrintSlots: settings.MaxPrintSlots,
            TimeZoneId: burn?.TimeZoneId,
            CreatedAt: settings.CreatedAt,
            UpdatedAt: settings.UpdatedAt);
    }

    public async Task<IReadOnlyList<EventSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default)
    {
        // At most one active burn; a list keeps the admin picker forward-compatible.
        var active = await settingsService.GetActiveEventSettingsAsync(ct);
        return active is null ? [] : [active];
    }

    public Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => settingsService.GetEventSettingsByIdAsync(id, ct);

    public async Task<bool> SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        var burn = await settingsService.GetEventSettingsByIdAsync(eventSettingsId, ct);
        if (burn is null)
        {
            logger.LogWarning("Guide settings save rejected: event settings {EventSettingsId} no longer exist", eventSettingsId);
            return false;
        }

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        var now = clock.GetCurrentInstant();

        var settings = new EventGuideSettings
        {
            Id = existingId ?? Guid.NewGuid(),
            EventSettingsId = eventSettingsId,
            SubmissionOpenAt = ToInstant(submissionOpenAt, tz),
            SubmissionCloseAt = ToInstant(submissionCloseAt, tz),
            GuidePublishAt = ToInstant(guidePublishAt, tz),
            MaxPrintSlots = maxPrintSlots,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.UpsertGuideSettingsAsync(settings, ct);
        return true;
    }

    public async Task<IReadOnlyList<EventCategoryView>> GetActiveCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await repo.GetActiveCategoriesAsync(ct);
        return categories.Select(ToCategoryView).ToList();
    }

    public async Task<IReadOnlyList<EventCategoryManageInfo>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await repo.GetAllCategoriesAsync(ct);
        return categories.Select(c => new EventCategoryManageInfo(
            c.Id, c.Name, c.Slug, c.IsSensitive, c.DisplayOrder, c.IsActive, c.Events.Count)).ToList();
    }

    public async Task<EventCategoryView?> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var category = await repo.GetCategoryAsync(id, ct);
        return category is null ? null : ToCategoryView(category);
    }

    public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default)
        => repo.CategorySlugExistsAsync(slug, excludeId, ct);

    public async Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default)
        => await repo.GetMaxCategoryOrderAsync(ct) + 1;

    public Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => repo.AddCategoryAsync(category, ct);

    public Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => repo.SaveCategoryAsync(category, ct);

    public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
        => repo.DeleteCategoryAsync(id, ct);

    public Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
        => repo.SwapCategoryOrderAsync(id, direction, ct);

    public async Task<IReadOnlyList<EventVenueView>> GetActiveVenuesAsync(CancellationToken ct = default)
    {
        var venues = await repo.GetActiveVenuesAsync(ct);
        return venues.Select(ToVenueView).ToList();
    }

    public async Task<IReadOnlyList<EventVenueManageInfo>> GetAllVenuesAsync(CancellationToken ct = default)
    {
        var venues = await repo.GetAllVenuesAsync(ct);
        return venues.Select(v => new EventVenueManageInfo(
            v.Id, v.Name, v.Description, v.LocationDescription, v.DisplayOrder, v.IsActive, v.Events.Count)).ToList();
    }

    public async Task<EventVenueView?> GetVenueAsync(Guid id, CancellationToken ct = default)
    {
        var venue = await repo.GetVenueAsync(id, ct);
        return venue is null ? null : ToVenueView(venue);
    }

    public async Task<int> GetNextVenueOrderAsync(CancellationToken ct = default)
        => await repo.GetMaxVenueOrderAsync(ct) + 1;

    public Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => repo.AddVenueAsync(venue, ct);

    public Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => repo.SaveVenueAsync(venue, ct);

    public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
        => repo.DeleteVenueAsync(id, ct);

    public Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default)
        => repo.SwapVenueOrderAsync(id, direction, ct);

    private static EventCategoryView ToCategoryView(EventCategory c) => new(
        Id: c.Id,
        Name: c.Name,
        Slug: c.Slug,
        IsSensitive: c.IsSensitive,
        DisplayOrder: c.DisplayOrder,
        IsActive: c.IsActive);

    private static EventVenueView ToVenueView(EventVenue v) => new(
        Id: v.Id,
        Name: v.Name,
        Description: v.Description,
        LocationDescription: v.LocationDescription,
        DisplayOrder: v.DisplayOrder,
        IsActive: v.IsActive);
}
