using NodaTime;

namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record VolunteerExportModel(
    string MethodologyBlurb,
    string FilterSummary,
    Instant GeneratedAtUtc,
    string GeneratedByName,
    IReadOnlyList<LocalDate> Days,
    IReadOnlyList<DepartmentGroup> Groups,
    IReadOnlyList<int> TotalsPerDay,
    string SuggestedFileName);
