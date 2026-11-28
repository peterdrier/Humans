using JetBrains.Annotations;

namespace Humans.Events.Models;

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
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
    int? PriorityRank);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record GuideEventCategoryApiDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record GuideEventCampApiDto(Guid Id, string? Name);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record GuideEventVenueApiDto(Guid Id, string Name);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record GuideCampApiDto(Guid Id, string? Name, string? Slug);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record GuideCampDetailApiDto(
    Guid Id,
    string? Name,
    string? Slug,
    IReadOnlyList<GuideEventApiDto> Events);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record GuideCategoryApiDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder);
