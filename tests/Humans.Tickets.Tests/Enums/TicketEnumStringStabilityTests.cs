using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Tests.Enums;

/// <summary>
/// The Tickets half of <c>Humans.Domain.Tests</c>' <c>EnumStringStabilityTests</c>, moved
/// here at the section's G5 move: <see cref="TicketSyncStatus"/> and
/// <see cref="TicketTransferStatus"/> / <see cref="TicketTransferVendorResult"/> are internal
/// to <c>Humans.Tickets</c>, and <see cref="TicketAttendeeStatus"/> /
/// <see cref="TicketPaymentStatus"/> live on the contracts leaf — none of them is nameable
/// from <c>Humans.Domain.Tests</c> any more (G5-SECTION-TEMPLATE.md step 5, Budget's rule).
/// </summary>
/// <remarks>
/// All five are persisted with <c>HasConversion&lt;string&gt;()</c>, so a rename leaves the
/// old string in the column. This row is the only thing between that and a silent data
/// mismatch — it moves with the enum, it is never deleted. A <c>[Fact]</c> rather than a
/// theory: a public test method cannot take an internal enum type as a parameter (CS0051,
/// Issues' fold).
/// </remarks>
public class TicketEnumStringStabilityTests
{
    [HumansFact]
    public void StringStoredTicketEnums_MemberNames_MustMatchExpected()
    {
        AssertNames(typeof(TicketSyncStatus), ["Idle", "Running", "Error"]);
        AssertNames(typeof(TicketPaymentStatus), ["Paid", "Pending", "Refunded"]);
        AssertNames(typeof(TicketAttendeeStatus), ["Valid", "Void", "CheckedIn"]);
        AssertNames(typeof(TicketTransferStatus), ["Pending", "Approved", "Rejected", "Cancelled"]);
        AssertNames(typeof(TicketTransferVendorResult),
            ["NotAttempted", "Succeeded", "VoidSucceededIssueFailed", "Failed"]);
    }

    private static void AssertNames(Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);
        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                "If you renamed it, create a DB migration to UPDATE the old values.");
        }
    }
}
