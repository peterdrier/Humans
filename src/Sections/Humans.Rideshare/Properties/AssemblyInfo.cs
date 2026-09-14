using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Rideshare.Tests, needs to see
// IRideshareRepository and IRouteProvider to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
