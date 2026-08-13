using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names. The
// adapter has no controllers, so the MVC feature-provider half of the marker is inert
// here — it is carried for the analyzers and for the discovered-sections boot log.
[assembly: Section("TicketTailor")]

// Castle DynamicProxy, behind NSubstitute in Humans.TicketTailor.Tests.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
