using Humans.Base.Interfaces;
using Humans.Monitor.Contracts;
using Humans.Monitor.Jobs;
using Humans.Monitor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Monitor;

/// <summary>
/// Monitor's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();

        services.AddScoped<DriveActivityMonitorJob>();
    }
}
