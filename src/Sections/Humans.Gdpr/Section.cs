using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Gdpr;

/// <summary>
/// Gdpr's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// One registration: the export orchestrator. Gdpr owns no tables, so there is no
/// <c>AddSectionDbContext</c> call and no repository — the service is a pure fan-out over
/// every registered <see cref="IUserDataContributor"/>, each of which is registered by the
/// section that owns the data. The line moved here out of Shell's
/// <c>GdprSectionExtensions.AddGdprSection</c>, which is deleted.
/// <para>
/// The contributor <em>forwarding factories</em> deliberately did not move: each
/// <c>services.AddScoped&lt;IUserDataContributor&gt;(sp =&gt; sp.GetRequiredService&lt;X&gt;())</c>
/// line belongs to the section that owns <c>X</c> and is registered beside it, in that
/// section's own <c>Section.Register</c> or Shell extension. Gdpr registering them would be
/// the parking-lot mistake step 4 warns about, and would need it to name thirteen sections'
/// internal service types.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // GDPR export orchestrator — pure fan-out over every IUserDataContributor.
        services.AddScoped<IGdprExportService, GdprExportService>();
    }
}
