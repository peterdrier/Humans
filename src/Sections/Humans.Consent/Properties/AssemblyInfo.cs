using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Consent.Tests, needs to see the
// internal section types to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
