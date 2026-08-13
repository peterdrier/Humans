using NodaTime;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services.Dtos;

internal sealed record VolunteerExportModel(
    string MethodologyBlurb,
    string FilterSummary,
    Instant GeneratedAtUtc,
    string GeneratedByName,
    IReadOnlyList<LocalDate> Days,
    IReadOnlyList<DepartmentGroup> Groups,
    IReadOnlyList<int> TotalsPerDay,
    string SuggestedFileName);
