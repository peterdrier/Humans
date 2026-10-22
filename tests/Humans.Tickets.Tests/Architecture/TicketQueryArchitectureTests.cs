using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NodaTime;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Stores;
using Humans.Tickets.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.Tickets.Tests.Architecture;

/// <summary>
/// The inner <see cref="TicketQueryService"/> reads through <see cref="ITicketRepository"/> and never
/// takes <c>IMemoryCache</c>; the <see cref="CachingTicketQueryService"/> decorator owns the cached slices.
/// </summary>
public class TicketQueryArchitectureTests
{
    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketManagement()
    {
        typeof(ITicketManagement).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue();
    }

    /// <summary>
    /// The services that write cached ticket rows (sync + merge fold, transfers, contact
    /// import, GDPR erasure) must resolve to the decorator, which evicts on its own write
    /// pass-through. A registration that bound one of them straight to its inner would
    /// write behind the cache with nothing left to evict it.
    /// </summary>
    [HumansFact]
    public void WritePaths_ResolveToTheCachingDecorator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IClock>());
        new Section().Register(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var decorator = scope.ServiceProvider.GetRequiredService<CachingTicketQueryService>();

        scope.ServiceProvider.GetRequiredService<ITicketSync>().Should().BeSameAs(decorator);
        scope.ServiceProvider.GetRequiredService<ITicketSyncService>().Should().BeSameAs(decorator);
        scope.ServiceProvider.GetRequiredService<ITicketTransferService>().Should().BeSameAs(decorator);
        scope.ServiceProvider.GetRequiredService<ITicketTransferQueue>().Should().BeSameAs(decorator);
        scope.ServiceProvider.GetRequiredService<IAttendeeContactImportService>().Should().BeSameAs(decorator);

        // The GDPR and merge fan-outs hold other sections' contributors too; invoke only
        // this section's forwarding factories.
        FanoutTargets<IUserDataContributor>(services, scope.ServiceProvider).Should().Contain(decorator);
        FanoutTargets<IUserMerge>(services, scope.ServiceProvider).Should().Contain(decorator);
    }

    private static IEnumerable<object> FanoutTargets<T>(IServiceCollection services, IServiceProvider sp) =>
        services
            .Where(d => d.ServiceType == typeof(T) && !d.IsKeyedService && d.ImplementationFactory is not null)
            .Select(d =>
            {
                try { return d.ImplementationFactory!(sp); }
                catch (InvalidOperationException) { return null; } // another fan-out member's unregistered deps
            })
            .OfType<object>()
            .ToList();

    /// <summary>
    /// <see cref="ITicketManagement"/> and <see cref="ITicketService"/> resolve to one singleton from the
    /// production <c>Section.Register</c>, not a test-built mirror of it.
    /// </summary>
    [HumansFact]
    public void ITicketManagement_And_ITicketService_ResolveToSameInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IClock>());
        new Section().Register(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var fromManagement = scope.ServiceProvider.GetRequiredService<ITicketManagement>();
        var fromPublic = scope.ServiceProvider.GetRequiredService<ITicketService>();

        fromManagement.Should().BeOfType<CachingTicketQueryService>(
            because: "the cache decorator is the section's public face");
        ReferenceEquals(fromManagement, fromPublic).Should().BeTrue(
            because: "both interfaces must expose the same singleton so the cache is shared");
    }
}
