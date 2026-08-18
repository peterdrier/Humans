using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Finance.Tests, needs to see
// IHoldedRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
