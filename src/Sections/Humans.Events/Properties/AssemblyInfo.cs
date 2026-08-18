using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Events.Tests, needs to see
// IEventRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
