using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Debug;

/// <summary>
/// Debug's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <see cref="Register"/> adds only <see cref="DataMigrationService"/>, the host for every
/// section's data migrations: every singleton the diagnostics pages read is registered by its
/// owner, and the section owns no tables. The class also ships so Shell's section discovery
/// finds the assembly — its discovered-sections log is the first thing to read when a page 404s.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Scoped: the migrations' delegates resolve the request's services.
        services.AddScoped<DataMigrationService>();
    }
}
