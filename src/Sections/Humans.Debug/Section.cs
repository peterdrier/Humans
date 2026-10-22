using Humans.Base.Interfaces;
using Humans.Debug.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Debug;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Scoped: the migrations' delegates resolve the request's services.
        services.AddScoped<DataMigrationService>();
    }
}
