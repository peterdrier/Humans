using System.Runtime.CompilerServices;

// NSubstitute's proxy needs to see IExpenseRepository.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
