using System.Runtime.CompilerServices;

// NSubstitute's proxy needs to see IHoldedRepository.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
