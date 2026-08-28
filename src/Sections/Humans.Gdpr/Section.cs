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
/// One registration: the subject-rights orchestrator (export + erasure). Gdpr owns no tables, so there is no
/// <c>AddSectionDbContext</c> call and no repository — the service is a pure fan-out over
/// every registered <see cref="IUserDataContributor"/>, each of which is registered by the
/// section that owns the data.
/// <para>
/// The contributor <em>forwarding factories</em> deliberately do not live here: each
/// <c>services.AddScoped&lt;IUserDataContributor&gt;(sp =&gt; sp.GetRequiredService&lt;X&gt;())</c>
/// line belongs beside the service that owns <c>X</c>, in that section's own
/// <c>Section.Register</c>. Registering them here would make Gdpr name every other
/// section's internal service types.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IGdprService, GdprService>();
    }
}
