using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NodaTime;

namespace Humans.Tickets.Tests.Services;

/// <summary>
/// Vendor reads and cache stats use the same caching decorator through the production registration.
/// Resolving these paths creates no database context.
/// </summary>
public sealed class CachingTicketVendorServiceRegistrationTests
{
    [HumansFact]
    public void Register_VendorReads_ShareCacheStatsDecorator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddLogging();

        var vendor = Substitute.For<ITicketVendorService>();
        services.AddKeyedScoped<ITicketVendorService>(TicketVendorServiceKeys.InnerServiceKey, (_, _) => vendor);
        new Section().Register(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var decorator = provider.GetRequiredService<CachingTicketVendorService>();
        provider.GetRequiredService<ITicketVendorService>().Should().BeSameAs(decorator);
        provider.GetRequiredService<ICacheStats>().Should().BeSameAs(decorator.EventSummaryCacheStats);
    }
}
