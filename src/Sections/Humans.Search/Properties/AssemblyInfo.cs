using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Search.Tests, needs to see the internal
// section types to proxy them (ISearchService).
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
