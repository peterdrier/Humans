using Humans.Base.Interfaces;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// A no-op <see cref="IHumansMetrics"/> for tests that depend on the metrics surface but
/// don't assert on it.
/// </summary>
/// <remarks>
/// Was a real <c>HumansMetricsService</c> until G5 lane 5b-6 (nobodies-collective/Humans#866)
/// moved that class to <c>Humans.Web</c>, which a section test may not reference.
/// </remarks>
internal static class TestMetrics
{
    public static IHumansMetrics Create() => Substitute.For<IHumansMetrics>();
}
