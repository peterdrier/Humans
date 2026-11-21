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
/// Vendor reads and check-in mirroring use the same caching decorator through the production registration.
/// Resolving these paths creates no database context.
/// </summary>
public sealed class CachingTicketVendorServiceRegistrationTests
{
    [HumansFact]
    public async Task VendorReadsMirrorAndCacheStats_UseSameDecoratorAndDispatchCheckInOnce()
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
        using var scope = provider.CreateScope();
        var mirror = scope.ServiceProvider.GetRequiredService<ITicketVendorMirror>();
        mirror.Should().BeSameAs(decorator);
        var occurredAt = Instant.FromUtc(2026, 7, 1, 12, 0);
        var ct = Xunit.TestContext.Current.CancellationToken;

        await mirror.CreateCheckInAsync("ticket", occurredAt, ct);

        await vendor.Received(1).CreateCheckInAsync("ticket", occurredAt, ct);
    }
}
