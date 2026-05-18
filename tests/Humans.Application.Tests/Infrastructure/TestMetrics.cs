using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// Builds a <see cref="HumansMetricsService"/> with no-op collaborators for tests
/// that depend on the metrics surface but don't assert on it. Centralized so a new
/// ctor argument on <see cref="HumansMetricsService"/> only touches one site.
/// </summary>
internal static class TestMetrics
{
    public static HumansMetricsService Create(IUserActivityTracker? tracker = null) =>
        new(
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<HumansMetricsService>.Instance,
            tracker ?? Substitute.For<IUserActivityTracker>());
}
