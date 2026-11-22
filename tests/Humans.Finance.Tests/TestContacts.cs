using Humans.Holded.Contracts;
using NodaTime;
using NSubstitute;

namespace Humans.Finance.Tests;

/// <summary>Synced contact mirrors for tests.</summary>
internal static class TestContacts
{
    public static HoldedContactMirror Synced(IEnumerable<HoldedContactDto> contacts) =>
        new(Instant.FromUtc(2026, 1, 1, 0, 0), contacts.ToList());

    /// <summary>An <see cref="IHoldedService"/> whose contact mirror has never synced; tests stub their own.</summary>
    public static IHoldedService Holded()
    {
        var holded = Substitute.For<IHoldedService>();
        holded.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(new HoldedContactMirror(null, []));
        return holded;
    }
}
