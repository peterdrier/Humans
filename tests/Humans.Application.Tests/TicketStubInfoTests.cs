using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Domain.Enums;
using Humans.Testing;
using NodaTime;

namespace Humans.Application.Tests;

/// <summary>
/// Covers <see cref="TicketStubInfo.From"/> — the single mapper that stamps the
/// holder's Early Entry date onto a stub, used by the homepage strip and the
/// transfer wizard so the EE pill can't be present on one surface and dropped on
/// another.
/// </summary>
public class TicketStubInfoTests
{
    private static MyAttendeeRowDto Row(bool pending = false, Guid? pendingId = null) => new(
        AttendeeId: Guid.NewGuid(),
        AttendeeName: "Ada Lovelace",
        AttendeeEmail: "ada@example.com",
        VendorTicketId: "TKT-001",
        TicketTypeName: "GA",
        Status: TicketAttendeeStatus.Valid,
        IsCurrentOwner: true,
        CanSendTransfer: true,
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
        stub.VendorTicketId.Should().Be(row.VendorTicketId);
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
