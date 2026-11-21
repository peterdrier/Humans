using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Roles.Tests, needs to see IRoleRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
