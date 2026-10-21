using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Grants.Tests, needs to see
// IGrantRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
