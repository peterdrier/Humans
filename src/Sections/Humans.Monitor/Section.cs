using Humans.Base.Interfaces;
using Humans.Monitor.Contracts;
using Humans.Monitor.Jobs;
using Humans.Monitor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Monitor;

public sealed class Section : ISection, ISectionJobs
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();

        services.AddScoped<DriveActivityMonitorJob>();
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "monitor-drive-activity", typeof(DriveActivityMonitorJob), "0 * * * *");
    }
}
