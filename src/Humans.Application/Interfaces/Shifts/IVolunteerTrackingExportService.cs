using Humans.Application.DTOs.VolunteerTrackingExport;

namespace Humans.Application.Interfaces.Shifts;

public interface IVolunteerTrackingExportService : IApplicationService
{
    Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct);
}
