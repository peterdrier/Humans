using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Tickets;

/// <summary>Help-widget access matrix for <c>/Ticket</c> — was a row in Base's deleted table.</summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Tickets",
            SectionName = "Tickets",
            Order = 70,
            Roles = ["Board", "TicketAdmin"],
            Features =
            [
                Of("View tickets & orders", ("Board", Allowed), ("TicketAdmin", Allowed)),
                Of("Sync operations", ("Board", Denied), ("TicketAdmin", Allowed)),
                Of("Discount codes", ("Board", Allowed), ("TicketAdmin", Allowed)),
            ]
        }
    ];
}
