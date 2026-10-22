using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Tickets.Tests.Services;

/// <summary>
/// <see cref="ITicketVendorService"/> and its <see cref="ICacheStats"/> both forward to one
/// <see cref="CachingTicketVendorService"/> singleton. Mirrors <c>Section.cs</c>'s registration lines
/// rather than running the section, which needs a live database via <c>AddSectionDbContext</c>.
/// </summary>
public sealed class CachingTicketVendorServiceRegistrationTests
{
    [HumansFact]
    public void VendorServiceAndCacheStats_ResolveToTheSameSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddLogging();

        services.AddSingleton<CachingTicketVendorService>();
        services.AddSingleton<ITicketVendorService>(sp => sp.GetRequiredService<CachingTicketVendorService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTicketVendorService>().EventSummaryCacheStats);

        using var provider = services.BuildServiceProvider();

        var decorator = provider.GetRequiredService<CachingTicketVendorService>();
        provider.GetRequiredService<ITicketVendorService>().Should().BeSameAs(decorator);
        provider.GetRequiredService<ICacheStats>().Should().BeSameAs(decorator.EventSummaryCacheStats);
    }
}
