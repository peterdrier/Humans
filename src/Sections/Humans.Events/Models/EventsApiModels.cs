namespace Humans.Events.Models;

internal sealed record GuideEventApiDto(
    Guid Id,
    string Title,
    string Description,
    GuideEventCategoryApiDto Category,
    string StartAt,
    int DurationMinutes,
    int DayOffset,
    bool IsRecurring,
    GuideEventCampApiDto? Camp,
    GuideEventVenueApiDto? Venue,
    string? LocationNote,
    string? Host,
    int PriorityRank);

internal sealed record GuideEventCategoryApiDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive);

internal sealed record GuideEventCampApiDto(Guid Id, string? Name);

internal sealed record GuideEventVenueApiDto(Guid Id, string Name);

internal sealed record GuideCampApiDto(Guid Id, string? Name, string? Slug);

internal sealed record GuideCampDetailApiDto(
    Guid Id,
    string? Name,
    string? Slug,
    IReadOnlyList<GuideEventApiDto> Events);

internal sealed record GuideCategoryApiDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder);
