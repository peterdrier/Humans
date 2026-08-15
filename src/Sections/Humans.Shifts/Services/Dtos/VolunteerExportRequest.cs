using NodaTime;
using Humans.Domain.Enums;
using Humans.Shifts.Services.Dtos;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Services.Dtos;

internal sealed record VolunteerExportRequest(
    Guid EventSettingsId,
    Guid? DepartmentId,
    LocalDate StartDate,
    LocalDate EndDate,
    ShiftPeriod? Period,
    string ActorPlayaName,
    Instant GeneratedAtUtc);
