namespace Humans.Tickets.Contracts;

/// <summary>On the leaf so the vendor adapter section can register the keyed inner without seeing Tickets' internals.</summary>
public static class TicketVendorServiceKeys
{
    public const string InnerServiceKey = "ticket-vendor-inner";
}
