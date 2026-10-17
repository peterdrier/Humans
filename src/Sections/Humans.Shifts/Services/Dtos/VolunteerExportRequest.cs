using NodaTime;
using Humans.Shifts.Contracts;
namespace Humans.Shifts.Services.Dtos;

internal sealed record VolunteerExportRequest(
    Guid EventSettingsId,
    Guid? DepartmentId,
    LocalDate StartDate,
    LocalDate EndDate,
    ShiftPeriod? Period,
    Guid ActorUserId,
    string ActorPlayaName,
    Instant GeneratedAtUtc);
