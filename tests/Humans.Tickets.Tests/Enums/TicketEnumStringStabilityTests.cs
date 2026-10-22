using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Tests.Enums;

/// <summary>
/// All five are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the OLD
/// string in the column. A rename needs a migration that UPDATEs the stored values. A <c>[Fact]</c>
/// rather than a theory: a public test method cannot take an internal enum (CS0051).
/// </summary>
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
