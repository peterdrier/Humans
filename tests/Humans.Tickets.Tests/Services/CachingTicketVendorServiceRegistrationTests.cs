using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Tickets.Tests.Services;

/// <summary>
/// Pins the DI shape Humans.Tickets' own <c>Section.cs</c> registers for the vendor caching
/// decorator (moved there from <c>Humans.TicketTailor</c>, which never named it):
/// <see cref="ITicketVendorService"/>, <see cref="ITicketVendorCacheInvalidator"/> and its
/// <see cref="ICacheStats"/> all forward to one <see cref="CachingTicketVendorService"/>
/// singleton. Mirrors the registration lines directly rather than exercising the full section
/// (which needs a live database via <c>AddSectionDbContext</c>).
/// </summary>
public sealed class CachingTicketVendorServiceRegistrationTests
{
    [HumansFact]
    public void VendorServiceAndInvalidatorAndCacheStats_AllResolveToTheSameSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddLogging();

        services.AddSingleton<CachingTicketVendorService>();
        services.AddSingleton<ITicketVendorService>(sp => sp.GetRequiredService<CachingTicketVendorService>());
        services.AddSingleton<ITicketVendorCacheInvalidator>(sp => sp.GetRequiredService<CachingTicketVendorService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketVendorService>().EventSummaryCacheStats);

        using var provider = services.BuildServiceProvider();

        var decorator = provider.GetRequiredService<CachingTicketVendorService>();
        provider.GetRequiredService<ITicketVendorService>().Should().BeSameAs(decorator);
        provider.GetRequiredService<ITicketVendorCacheInvalidator>().Should().BeSameAs(decorator);
        provider.GetRequiredService<ICacheStats>().Should().BeSameAs(decorator.EventSummaryCacheStats);
    }
}
