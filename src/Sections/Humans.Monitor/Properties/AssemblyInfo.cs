using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Monitor.Tests, needs to see the internal
// service and controller to construct them against substituted read interfaces.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
