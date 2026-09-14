using AwesomeAssertions;
using Humans.TicketTailor.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Humans.TicketTailor.Tests;

/// <summary>The environment name decides the binding, never the presence of a key.</summary>
public class SectionRegistrationTests
{
    [HumansFact]
    public void Production_BindsTheLiveClient()
    {
        using var provider = Build("Production");

        provider.GetRequiredService<ITicketVendorService>().Should().BeOfType<TicketTailorService>();
    }

    [HumansTheory]
    [Xunit.InlineData("Development")]
    [Xunit.InlineData("Staging")]
    [Xunit.InlineData("production")]
    [Xunit.InlineData(null)]
    public void AnythingElse_BindsTheStubWithPlaceholderSettings(string? environment)
    {
        using var provider = Build(environment);

        provider.GetRequiredService<ITicketVendorService>().Should().BeOfType<StubTicketVendorService>();

        var settings = provider.GetRequiredService<IOptions<TicketVendorSettings>>().Value;
        settings.EventId.Should().Be("stub-event");
        settings.ApiKey.Should().Be("stub");
        settings.IsConfigured.Should().BeTrue();
    }

    [HumansFact]
    public void NonProduction_WithARealKey_StillBindsTheStubAndKeepsTheKey()
    {
        using var provider = Build("Development", services =>
            services.Configure<TicketVendorSettings>(o => o.ApiKey = "sk_live_real"));

        provider.GetRequiredService<ITicketVendorService>().Should().BeOfType<StubTicketVendorService>();
        provider.GetRequiredService<IOptions<TicketVendorSettings>>().Value.ApiKey.Should().Be("sk_live_real");
    }

    private static ServiceProvider Build(string? environment, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddOptions();
        configure?.Invoke(services);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { [HostDefaults.EnvironmentKey] = environment })
            .Build();
        new Section().Register(services, config);

        return services.BuildServiceProvider();
    }
}
