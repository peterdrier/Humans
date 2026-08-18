using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Holded.Tests, needs to see
// IHoldedMirrorRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
