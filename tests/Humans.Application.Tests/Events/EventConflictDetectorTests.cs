using AwesomeAssertions;
using Humans.Application.Events;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Events;

public sealed class EventConflictDetectorTests
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 7, 1, 10, 0);

    [HumansTheory]
    [InlineData(0, 60, 30, 60, true)]    // partial overlap
    [InlineData(0, 60, 0, 30, true)]     // containment
    [InlineData(0, 60, 60, 60, false)]   // back-to-back: half-open, no conflict
    [InlineData(0, 60, 90, 60, false)]   // disjoint
    public void Overlaps_HalfOpenIntervalSemantics(
        int aOffsetMinutes, int aDuration, int bOffsetMinutes, int bDuration, bool expected)
    {
        var a = T0.Plus(Duration.FromMinutes(aOffsetMinutes));
        var b = T0.Plus(Duration.FromMinutes(bOffsetMinutes));

        EventConflictDetector.Overlaps(a, aDuration, b, bDuration).Should().Be(expected);
        // Symmetric.
        EventConflictDetector.Overlaps(b, bDuration, a, aDuration).Should().Be(expected);
    }

    [HumansFact]
    public void FindConflictingIndexes_FlagsBothSidesOfEachOverlap_AndNothingElse()
    {
        // [0] 10:00-11:00 overlaps [1] 10:30-11:30; [2] 12:00-13:00 is clear.
        var items = new[]
        {
            (StartAt: T0, Minutes: 60),
            (StartAt: T0.Plus(Duration.FromMinutes(30)), Minutes: 60),
            (StartAt: T0.Plus(Duration.FromMinutes(120)), Minutes: 60),
        };

        var conflicted = EventConflictDetector.FindConflictingIndexes(
            items, i => i.StartAt, i => i.Minutes);

        conflicted.Should().BeEquivalentTo([0, 1]);
    }
}
