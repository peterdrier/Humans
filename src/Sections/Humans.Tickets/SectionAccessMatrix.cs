using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Tickets;

internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Tickets",
            SectionName = "Tickets",
            Order = 70,
            Roles = ["Board", "TicketsAdmin"],
            Features =
            [
                Of("View tickets & orders", ("Board", Allowed), ("TicketsAdmin", Allowed)),
                Of("Sync operations", ("Board", Denied), ("TicketsAdmin", Allowed)),
                Of("Discount codes", ("Board", Allowed), ("TicketsAdmin", Allowed)),
            ]
        }
    ];
}
