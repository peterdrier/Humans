using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Tickets.Tests, needs to see the
// internal ITicketManagement / ITicketRepository and friends to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
