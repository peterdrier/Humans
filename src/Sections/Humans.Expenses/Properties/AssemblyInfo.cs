using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Expenses.Tests, needs to see the
// internal IExpenseRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
