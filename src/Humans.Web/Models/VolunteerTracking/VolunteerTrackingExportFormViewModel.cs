using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.VolunteerTracking;

/// <summary>
/// State for the Export card on the Volunteer Tracking page. Populated by the
/// controller from <c>IShiftManagementService.GetDepartmentsWithRotasAsync</c>
/// and rendered by <c>_ExportCard.cshtml</c>; the form posts back to
/// <c>VolunteerTrackingController.ExportXlsx</c>.
/// </summary>
public sealed class VolunteerTrackingExportFormViewModel
{
    public IReadOnlyList<(Guid TeamId, string TeamName)> Departments { get; init; } = [];
    public Guid? SelectedDepartmentId { get; init; }
    public ShiftPeriod? SelectedPeriod { get; init; }
    public BuildSubPeriod? SelectedSubPeriod { get; init; }
    public LocalDate? StartDate { get; init; }
    public LocalDate? EndDate { get; init; }
}
