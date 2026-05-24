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
    Instant GeneratedAtUtc);
