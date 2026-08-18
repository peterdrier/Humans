using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.GoogleIntegration.Tests, needs to see the
// internal services, repositories and connector abstractions to substitute them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
