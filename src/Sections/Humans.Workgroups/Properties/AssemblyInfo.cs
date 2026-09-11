using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Workgroups.Tests, needs to see
// IWorkgroupRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
