using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Agent.Tests, needs to see the internal
// section interfaces to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
