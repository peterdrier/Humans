using Humans.Base.Interfaces;
using Humans.Monitor.Contracts;
using Humans.Monitor.Jobs;
using Humans.Monitor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Monitor;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();

        services.AddScoped<DriveActivityMonitorJob>();
    }
}
