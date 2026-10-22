using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Cantina.Contracts;
using Humans.Cantina.Data;
using Humans.Cantina.Services;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Cantina;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CantinaDbContext>(sentinelTable: "cantina_dietary_profiles");
        services.AddScoped<IDietaryProfileRepository, DietaryProfileRepository>();
        services.AddScoped<DietaryProfileService>();
        services.AddScoped<IDietaryProfileManagement>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<ICantinaService>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<DietaryProfileService>());

        services.AddScoped<ICantinaRosterService, CantinaRosterService>();
    }
}
