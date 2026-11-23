using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Base.Demo;

/// <summary>
/// Registers demo pieces outside Production only, failing closed: <c>ISection.Register</c> gets no
/// <see cref="IHostEnvironment"/>, so an environment name configuration doesn't carry registers nothing.
/// </summary>
public static class DemoRegistration
{
    public static bool IsDemoEnvironment(IConfiguration configuration)
    {
        var environmentName = configuration[HostDefaults.EnvironmentKey];
        return !string.IsNullOrWhiteSpace(environmentName)
            && !string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
    }

    public static IServiceCollection AddDemoStory<TStory>(this IServiceCollection services, IConfiguration configuration)
        where TStory : class, IDemoStory
    {
        if (IsDemoEnvironment(configuration))
            services.AddScoped<IDemoStory, TStory>();
        return services;
    }

    public static IServiceCollection AddDemoSafetyCheck<TCheck>(
        this IServiceCollection services, IConfiguration configuration)
        where TCheck : class, IDemoSafetyCheck
    {
        if (IsDemoEnvironment(configuration))
            services.AddScoped<IDemoSafetyCheck, TCheck>();
        return services;
    }
}
