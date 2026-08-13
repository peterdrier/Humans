using Humans.Shifts.Services.Dtos;
using Humans.Application.Interfaces;

namespace Humans.Shifts.Services;

internal interface IVolunteerTrackingExportService : IApplicationService
{
    Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct);
}
