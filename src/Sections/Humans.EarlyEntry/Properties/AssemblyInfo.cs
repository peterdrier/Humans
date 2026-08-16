using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.EarlyEntry.Tests, needs to see the
// internal section types to proxy them — the orchestrator's IEarlyEntryProvider fan-out and
// the decorator's inner IEarlyEntryService are both substituted there.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
