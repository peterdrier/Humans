using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Cantina.Tests, substitutes the
// cross-section read interfaces the roster service composes over.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
