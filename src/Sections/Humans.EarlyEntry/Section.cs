using Humans.Base.Interfaces;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Scoped and uncached: fresh every request, so no contributing section has to evict anything.
        services.AddScoped<IEarlyEntryService, EarlyEntryService>();
    }
}
