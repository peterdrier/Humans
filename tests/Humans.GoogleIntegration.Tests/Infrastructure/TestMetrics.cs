using Humans.Application.Interfaces;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// Builds a <see cref="HumansMetricsService"/> with no-op collaborators for tests
/// that depend on the metrics surface but don't assert on it.
/// </summary>
/// <remarks>
/// A copy of <c>Humans.Application.Tests</c>' helper of the same name rather than a shared
/// one: sharing it through <c>tests/Directory.Build.props</c> would push a
/// <c>Humans.Infrastructure</c> reference into every test project that compiles the shared
/// set, for a three-argument constructor call (Governance's split-the-helper rule,
/// G5-SECTION-TEMPLATE.md step 8).
/// </remarks>
internal static class TestMetrics
{
    public static HumansMetricsService Create(IUserActivityTracker? tracker = null) =>
        new(
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<HumansMetricsService>.Instance,
            tracker ?? Substitute.For<IUserActivityTracker>());
}
