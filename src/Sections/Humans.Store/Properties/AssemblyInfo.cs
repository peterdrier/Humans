using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Store.Tests, needs to see
// IStoreRepository to proxy it. Internal visibility is the point of the section
// boundary, and Castle requires this grant for an internal type either way — an
// internal interface is no more proxyable than an internal class.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
