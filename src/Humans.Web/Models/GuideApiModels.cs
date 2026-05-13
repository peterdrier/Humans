namespace Humans.Web.Models;

public sealed record GuideEventApiDto(
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
    string? SubmitterName,
    string? LocationNote,
    int PriorityRank);

public sealed record GuideEventCategoryApiDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive);

public sealed record GuideEventCampApiDto(Guid Id, string? Name);

public sealed record GuideEventVenueApiDto(Guid Id, string Name);

public sealed record GuideCampApiDto(Guid Id, string? Name, string? Slug);

public sealed record GuideCampDetailApiDto(
    Guid Id,
    string? Name,
    string? Slug,
    IReadOnlyList<GuideEventApiDto> Events);

public sealed record GuideCategoryApiDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder);
