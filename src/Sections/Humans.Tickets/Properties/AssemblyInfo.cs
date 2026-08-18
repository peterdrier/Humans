using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Tickets.Tests, needs to see the
// internal ITicketService / ITicketRepository / ITicketVendorGateway to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
