using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Tasks.Tests, needs to see the internal interfaces to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
