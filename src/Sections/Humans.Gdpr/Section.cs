using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Gdpr;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IGdprService, GdprService>();
    }
}
