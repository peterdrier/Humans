using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Scaffolding for the volunteer-tracking export pipeline. The collaborators
/// are wired so the follow-up task can implement <see cref="BuildAsync"/> by
/// composing the repository read, EventSettings lookup, and user-info lookup
/// without touching DI. Throws <see cref="NotSupportedException"/> until then —
/// the Meziantou analyzer rejects <c>NotImplementedException</c> at build time.
/// </summary>
public sealed class VolunteerTrackingExportService(
    IVolunteerTrackingRepository repository,
    IShiftManagementService shiftManagementService,
    IUserService userService)
    : IVolunteerTrackingExportService
{
    private readonly IVolunteerTrackingRepository _repository = repository;
    private readonly IShiftManagementService _shiftManagementService = shiftManagementService;
    private readonly IUserService _userService = userService;

    public Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
        => throw new NotSupportedException(
            $"{nameof(VolunteerTrackingExportService)}.{nameof(BuildAsync)} is scaffolding; implementation lands in the next task.");
}
