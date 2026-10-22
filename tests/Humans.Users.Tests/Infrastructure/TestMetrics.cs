using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Humans.Base.Interfaces;
using Humans.Web.Services;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>No-op collaborators, centralized so a new constructor argument touches one site.</summary>
internal static class TestMetrics
{
    public static HumansMetricsService Create(IUserActivityTracker? tracker = null) =>
        new(
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<HumansMetricsService>.Instance,
            tracker ?? Substitute.For<IUserActivityTracker>());
}
