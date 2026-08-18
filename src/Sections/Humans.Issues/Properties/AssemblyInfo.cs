using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Issues.Tests, needs to see the
// internal section types to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
