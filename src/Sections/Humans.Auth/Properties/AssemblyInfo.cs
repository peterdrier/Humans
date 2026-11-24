using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Auth.Tests, needs to see the
// internal ISystemRoleRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
