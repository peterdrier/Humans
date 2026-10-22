using NodaTime;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Models;

internal sealed class VolunteerTrackingExportFormViewModel
{
    public IReadOnlyList<(Guid TeamId, string TeamName)> Departments { get; init; } = [];
    public Guid? SelectedDepartmentId { get; init; }
    public ShiftPeriod? SelectedPeriod { get; init; }
    public BuildSubPeriod? SelectedSubPeriod { get; init; }
    public LocalDate? StartDate { get; init; }
    public LocalDate? EndDate { get; init; }
}
