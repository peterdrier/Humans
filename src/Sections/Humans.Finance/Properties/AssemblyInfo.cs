using System.Runtime.CompilerServices;

// NSubstitute's proxy needs to see IFinanceRepository.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
