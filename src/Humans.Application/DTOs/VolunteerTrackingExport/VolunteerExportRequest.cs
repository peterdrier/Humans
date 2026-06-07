using Humans.Application.Extensions;
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record VolunteerExportRequest(
    Guid EventSettingsId,
    Guid? DepartmentId,
    LocalDate StartDate,
    LocalDate EndDate,
    ShiftPeriod? Period,
    string ActorPlayaName,
    Instant GeneratedAtUtc)
{
    public string PeriodLabel => Period?.ToString() ?? "custom";

    public IReadOnlyList<LocalDate> EnumerateDays()
    {
        var count = NodaTime.Period.DaysBetween(StartDate, EndDate) + 1;
        var days = new LocalDate[count];
        for (var i = 0; i < count; i++) days[i] = StartDate.PlusDays(i);
        return days;
    }

    public string BuildFilterSummary(string? filteredTeamName)
    {
        var deptName = filteredTeamName ?? "All";
        return $"Department: {deptName} - Range: {StartDate} -> {EndDate} ({PeriodLabel})";
    }

    public string BuildSuggestedFileName(string? departmentSlug)
    {
        var prefix = departmentSlug is { Length: > 0 } slug
            ? $"volunteer-tracking-{slug}-"
            : "volunteer-tracking-";
        return $"{prefix}{StartDate.ToInvariantDate()}-to-{EndDate.ToInvariantDate()}.xlsx";
    }
}
