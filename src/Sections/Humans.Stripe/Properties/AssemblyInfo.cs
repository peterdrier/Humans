using System.Runtime.CompilerServices;

// Lets NSubstitute (Castle DynamicProxy) mock the section's internal types from its test project.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
