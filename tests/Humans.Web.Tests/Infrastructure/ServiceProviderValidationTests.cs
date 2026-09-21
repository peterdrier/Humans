using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Captive dependencies: a Singleton (or hosted service) that constructor-injects a Scoped
/// service. <c>Program.cs</c> builds the host with <c>ValidateScopes</c> /
/// <c>ValidateOnBuild</c>, so one of these throws at startup and the deployment answers 503
/// with nothing else failing first — the shipped section registrations are checked nowhere
/// else before a deploy. Singletons that need a Scoped service take
/// <c>IServiceScopeFactory</c> and open a scope per call (see <c>CachingCampService</c>).
/// </summary>
public sealed class ServiceProviderValidationTests
{
    [HumansFact]
    public void NoSingletonConstructorInjectsAScopedService()
    {
        var services = new ServiceCollection();
        Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(services, BuildMinimalConfiguration(), new StubHostEnvironment());

        var scopedServiceTypes = services
            .Where(d => d.Lifetime == ServiceLifetime.Scoped && !d.IsKeyedService)
            .Select(d => d.ServiceType)
            .ToHashSet();

        var captives = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton)
            .Select(d => d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)
            .Where(t => t is not null)
            .Distinct()
            .SelectMany(t => Constructors(t!)
                .SelectMany(c => c.GetParameters())
                .Where(p => p.GetCustomAttribute<FromKeyedServicesAttribute>() is null)
                .Where(p => scopedServiceTypes.Contains(p.ParameterType))
                .Select(p => $"{t!.Name}.{p.Name} ({p.ParameterType.Name})"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        captives.Should().BeEmpty(
            because: "a Singleton holding a Scoped service fails ValidateScopes at host "
                     + "startup — take IServiceScopeFactory and resolve per call instead");
    }

    private static IEnumerable<ConstructorInfo> Constructors(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

    private static IConfiguration BuildMinimalConfiguration()
    {
        var inMemory = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["Email:FromAddress"] = "humans@nobodies.team",
            ["Email:BaseUrl"] = "https://localhost",
            ["Email:SmtpHost"] = "localhost",
            ["GitHub:Owner"] = "stub",
            ["GitHub:Repository"] = "stub",
            ["GitHub:AccessToken"] = "stub",
            ["GoogleMaps:ApiKey"] = "stub",
            ["TicketVendor:EventId"] = "stub-event",
            ["TicketVendor:Provider"] = "stub"
        };

        var builder = new ConfigurationBuilder();
        builder.Add(new MemoryConfigurationSource { InitialData = inMemory });
        return builder.Build();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Humans.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
