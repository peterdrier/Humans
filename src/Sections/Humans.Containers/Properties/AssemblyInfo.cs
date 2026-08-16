using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Containers.Tests, needs to see
// IContainerRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
