using AwesomeAssertions;
using Humans.Tickets.Data;

namespace Humans.Tickets.Tests.Data;

/// <summary>
/// <see cref="TicketPiiTombstone.IsTombstoned"/> is the sync guard's fallback for rows
/// erased before <c>PiiErasedAt</c> existed — nobodies-collective/Humans#1178.
/// </summary>
public sealed class TicketPiiTombstoneTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [HumansFact]
    public void TombstonedName_IsRecognised()
    {
        TicketPiiTombstone.IsTombstoned(TicketPiiTombstone.Name, "real@example.com").Should().BeTrue();
    }

    [HumansFact]
    public void TombstonedEmail_IsRecognised()
    {
        TicketPiiTombstone.IsTombstoned("Real Name", TicketPiiTombstone.EmailFor(UserId)).Should().BeTrue();
    }

    [HumansFact]
    public void BothTombstoned_IsRecognised()
    {
        TicketPiiTombstone.IsTombstoned(TicketPiiTombstone.Name, TicketPiiTombstone.EmailFor(UserId)).Should().BeTrue();
    }

    [HumansFact]
    public void NeitherTombstoned_IsNotRecognised()
    {
        TicketPiiTombstone.IsTombstoned("Real Name", "real@example.com").Should().BeFalse();
    }

    [HumansFact]
    public void NullEmail_FallsBackToNameCheck()
    {
        TicketPiiTombstone.IsTombstoned(TicketPiiTombstone.Name, null).Should().BeTrue();
        TicketPiiTombstone.IsTombstoned("Real Name", null).Should().BeFalse();
    }
}
