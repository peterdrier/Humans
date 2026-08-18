using Humans.Shifts.Services.Dtos;
using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

internal interface IVolunteerTrackingExportService : IApplicationService
{
    Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct);
}
