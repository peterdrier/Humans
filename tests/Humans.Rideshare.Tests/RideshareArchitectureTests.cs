using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Models.Tables;
using Humans.Gdpr.Contracts;
using Humans.Rideshare.Controllers;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Rideshare.Tests;

/// <summary>
/// Architecture tests pinning the Rideshare section's DI shape: Singleton repository over
/// the context factory, the keyed inner service behind a Singleton caching decorator, the
/// GDPR contributor forwarded to that decorator, and the badge rows the section registers.
/// </summary>
public class RideshareArchitectureTests
{
    // ── Routes and policies ───────────────────────────────────────────────

    [HumansFact]
    public void RideshareRoutes_UseTheRideshareSlug()
    {
        RouteFor<RideshareController>().Should().Be("Rideshare");
        RouteFor<RideshareAdminController>().Should().Be("Rideshare/Admin");
        RouteFor<RideshareApiController>().Should().Be("api/rideshare");
    }

    [HumansFact]
    public void MemberAndApiSurfaces_RequireAppAccess()
    {
        // Docs/Rideshare.md "Members-only board": nothing here is anonymous.
        PolicyFor<RideshareController>().Should().Be(PolicyNames.AppAccess);
        PolicyFor<RideshareApiController>().Should().Be(PolicyNames.AppAccess);
    }

    [HumansFact]
    public void AdminSurface_RequiresAdminOnly()
    {
        PolicyFor<RideshareAdminController>().Should().Be(PolicyNames.AdminOnly,
            because: "settings, season stats and the day roster are Board tooling");
    }

    // ── DI shape ──────────────────────────────────────────────────────────

    [HumansFact]
    public void Repository_IsASingletonOverTheContextFactory()
    {
        var descriptor = Registrations().Single(d => d.ServiceType == typeof(IRideshareRepository));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            because: "the repository opens its own short-lived context per call through IDbContextFactory");
        descriptor.ImplementationType.Should().Be(typeof(RideshareRepository));
    }

    [HumansFact]
    public void InnerService_IsKeyedScoped_UnderTheDecoratorsKey()
    {
        var descriptor = Registrations().Single(d =>
            d.ServiceType == typeof(IRideshareService) && d.IsKeyedService);

        descriptor.ServiceKey.Should().Be(CachingRideshareService.InnerServiceKey);
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.KeyedImplementationType.Should().Be(typeof(RideshareService));
    }

    [HumansFact]
    public void InnerServiceKey_FollowsTheSectionInnerConvention()
    {
        CachingRideshareService.InnerServiceKey.Should().Be("rideshare-inner");
    }

    [HumansFact]
    public void Decorator_IsASingleton_AndIsWhatIRideshareServiceResolvesTo()
    {
        var services = Registrations();
        services.Single(d => d.ServiceType == typeof(CachingRideshareService)).Lifetime
            .Should().Be(ServiceLifetime.Singleton, because: "the snapshot cache lives on the instance");
        services.Single(d => d.ServiceType == typeof(IRideshareService) && !d.IsKeyedService).Lifetime
            .Should().Be(ServiceLifetime.Singleton);

        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRideshareService>()
            .Should().BeOfType<CachingRideshareService>()
            .And.BeSameAs(provider.GetRequiredService<CachingRideshareService>());
    }

    [HumansFact]
    public void UserDataContributor_IsForwardedToTheDecorator()
    {
        // Erasure empties rows the cache serves, so the GDPR fan-out must reach the
        // decorator — drop this forwarder and export/erasure silently skip Rideshare.
        var services = Registrations();
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserDataContributor));

        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IUserDataContributor>()
            .Should().BeSameAs(provider.GetRequiredService<CachingRideshareService>());
    }

    [HumansFact]
    public void Section_RegistersItsBadgeRows()
    {
        Registrations();

        var expected = new Dictionary<Enum, string>
        {
            [TripStatus.Active] = "bg-success",
            [TripStatus.Cancelled] = "bg-dark",
            [RequestStatus.Active] = "bg-success",
            [RequestStatus.Cancelled] = "bg-dark",
            [InterestStatus.Pending] = "bg-warning text-dark",
            [InterestStatus.Accepted] = "bg-success",
            [InterestStatus.Declined] = "bg-secondary",
            [InterestStatus.Withdrawn] = "bg-dark",
        };
        foreach (var (value, cssClass) in expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");
    }

    /// <summary>The section's own DI registrations, from <see cref="Section.Register"/>.</summary>
    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());
        return services;
    }

    private static string RouteFor<TController>() =>
        typeof(TController).GetCustomAttributes<RouteAttribute>(inherit: false).Single().Template;

    private static string? PolicyFor<TController>()
    {
        var authorize = typeof(TController).GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull(because: $"{typeof(TController).Name} must not be reachable anonymously");
        return authorize!.Policy;
    }
}
