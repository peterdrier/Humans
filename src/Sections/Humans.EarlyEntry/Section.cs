using Humans.Base.Interfaces;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry;

/// <summary>Discovered by the Shell; nothing names it.</summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Uncached: every read fans out over the providers live, so no other section
        // has to tell this one when a grant moves.
        services.AddScoped<IEarlyEntryService, EarlyEntryService>();
    }
}
