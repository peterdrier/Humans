using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Gate.Tests, needs to see the internal
// section interfaces to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
