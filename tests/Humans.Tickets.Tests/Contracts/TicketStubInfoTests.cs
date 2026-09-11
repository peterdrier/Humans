using AwesomeAssertions;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Tests.Contracts;

/// <summary>
/// Covers <c>TicketStubInfo.From</c> — the single mapper that stamps the holder's Early
/// Entry date onto a stub, used by the homepage strip and the transfer wizard so the EE
/// pill can't be present on one surface and dropped on another.
/// </summary>
public class TicketStubInfoTests
{
    private static UserTicketHoldingRow Row(bool pending = false, Guid? pendingId = null) => new(
        AttendeeId: Guid.NewGuid(),
        AttendeeName: "Ada Lovelace",
        AttendeeEmail: "ada@example.com",
        VendorTicketId: "TKT-001",
        TicketTypeName: "GA",
        Status: TicketAttendeeStatus.Valid,
        HasPendingOutgoingTransfer: pending,
        PendingTransferRequestId: pendingId);

    [HumansFact]
    public void From_StampsHolderEarlyEntry_AndMapsCoreFields()
    {
        var ee = new LocalDate(2026, 8, 24);
        var row = Row();

        var stub = TicketStubInfo.From(row, ee);

        stub.AttendeeName.Should().Be(row.AttendeeName);
        stub.AttendeeEmail.Should().Be(row.AttendeeEmail);
        stub.Status.Should().Be(row.Status);
        stub.EarlyEntryDate.Should().Be(ee);
    }

    [HumansFact]
    public void From_NullHolderEarlyEntry_LeavesEarlyEntryDateNull()
    {
        var stub = TicketStubInfo.From(Row(), holderEarlyEntry: null);

        stub.EarlyEntryDate.Should().BeNull();
    }

    [HumansFact]
    public void From_CarriesPendingTransferState()
    {
        var pendingId = Guid.NewGuid();

        var stub = TicketStubInfo.From(Row(pending: true, pendingId: pendingId), holderEarlyEntry: null);

        stub.HasPendingTransfer.Should().BeTrue();
        stub.PendingTransferRequestId.Should().Be(pendingId);
    }
}
