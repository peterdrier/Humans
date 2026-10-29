using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Debug;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, DebugAuditActions>();
        // Scoped: the migrations' delegates resolve the request's services.
        services.AddScoped<DataMigrationService>();
    }
}
