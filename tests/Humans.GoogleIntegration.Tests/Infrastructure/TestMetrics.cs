using Humans.Base.Interfaces;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// A no-op <see cref="IHumansMetrics"/> for tests that depend on the metrics surface but
/// don't assert on it.
/// </summary>
/// <remarks>
/// <c>HumansMetricsService</c> lives in <c>Humans.Web</c>, which a section test may not reference.
/// </remarks>
internal static class TestMetrics
{
    public static IHumansMetrics Create() => Substitute.For<IHumansMetrics>();
}
