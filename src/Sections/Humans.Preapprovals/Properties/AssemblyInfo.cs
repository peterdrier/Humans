using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Preapprovals.Tests, needs to see
// IPreapprovalRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
