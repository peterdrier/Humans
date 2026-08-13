using Humans.Application.Interfaces;
using Humans.Monitor.Contracts;
using Humans.Monitor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Monitor;

/// <summary>
/// Monitor's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// One registration: the Drive-activity monitor. The line came out of
/// <c>GoogleIntegrationSectionExtensions</c>, where it sat because the service's file did
/// (Governance's rule — the section that owns the file is not always the section that owns
/// the line).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();
    }
}
